// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.Devices.Client.Transport.Mqtt
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Security;
    using System.Net.WebSockets;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading;
    using System.Threading.Tasks;
    using DotNetty.Buffers;
    using DotNetty.Codecs.Mqtt;
    using DotNetty.Codecs.Mqtt.Packets;
    using DotNetty.Common.Concurrency;
    using DotNetty.Handlers.Tls;
    using DotNetty.Transport.Bootstrapping;
    using DotNetty.Transport.Channels;
    using DotNetty.Transport.Channels.Sockets;
    using Microsoft.Azure.Devices.Client.Common;
    using Microsoft.Azure.Devices.Client.Exceptions;
    using Microsoft.Azure.Devices.Client.Extensions;
    using Microsoft.Azure.Devices.Shared;
    using Microsoft.Practices.EnterpriseLibrary.TransientFaultHandling;
    using TransportType = Microsoft.Azure.Devices.Client.TransportType;

    sealed class MqttTransportHandler : TransportHandler
    {
        const int ProtocolGatewayPort = 8883;
        const int MaxMessageSize = 256 * 1024;

        [Flags]
        internal enum TransportState
        {
            NotInitialized = 1,
            Opening = 2,
            Open = 4,
            Subscribing = Open | 8,
            Receiving = Open | 16,
            Closed = 32,
            Error = 64
        }

        static readonly int GenerationPrefixLength = Guid.NewGuid().ToString().Length;

        readonly string generationId = Guid.NewGuid().ToString();

        static readonly ConcurrentObjectPool<string, IEventLoopGroup> EventLoopGroupPool =
            new ConcurrentObjectPool<string, IEventLoopGroup>(
                Environment.ProcessorCount,
                () => new MultithreadEventLoopGroup(() => new SingleThreadEventLoop("MQTTExecutionThread", TimeSpan.FromSeconds(1)), 1),
                TimeSpan.FromSeconds(5),
                elg => elg.ShutdownGracefullyAsync());

        readonly IPAddress serverAddress;
        readonly Func<IPAddress, int, Task<IChannel>> channelFactory;
        readonly Queue<string> completionQueue;
        readonly MqttIotHubAdapterFactory mqttIotHubAdapterFactory;
        readonly QualityOfService qos;

        readonly string eventLoopGroupKey;
        readonly object syncRoot = new object();
        readonly CancellationTokenSource disconnectAwaitersCancellationSource = new CancellationTokenSource();
        readonly RetryPolicy closeRetryPolicy;

        readonly SemaphoreSlim receivingSemaphore = new SemaphoreSlim(0);
        readonly ConcurrentQueue<Message> messageQueue;

        readonly TaskCompletionSource connectCompletion = new TaskCompletionSource();
        readonly TaskCompletionSource subscribeCompletionSource = new TaskCompletionSource();
        Func<Task> cleanupFunc;
        IChannel channel;
        Exception fatalException;

        int state = (int)TransportState.NotInitialized;
        TransportState State => (TransportState)Volatile.Read(ref this.state);

        // incoming topic names
        const string methodPostTopicFilter ="$iothub/methods/POST/#";
        const string methodPostTopicPrefix = "$iothub/methods/POST/";
        const string twinResponseTopicFilter = "$iothub/twin/res/#";
        const string twinResponseTopicPrefix = "$iothub/twin/res/";
        const string twinPatchTopicFilter =" $iothub/twin/PATCH/properties/desired/#";
        const string twinPatchTopicPrefix = " $iothub/twin/PATCH/properties/desired/";

        // outgoing topic names
        const string methodResponseTopic = "$iothub/methods/res/{0}/?$rid={1}";

        Func<MethodRequestInternal, Task> messageListener;

        internal MqttTransportHandler(IotHubConnectionString iotHubConnectionString)
            : this(iotHubConnectionString, new MqttTransportSettings(TransportType.Mqtt_Tcp_Only))
        {

        }

        internal MqttTransportHandler(IotHubConnectionString iotHubConnectionString, MqttTransportSettings settings, Func<MethodRequestInternal, Task> onMethodCallback = null)
            : this(iotHubConnectionString, settings, null)
        {
            this.messageListener = onMethodCallback;
        }

        internal MqttTransportHandler(IotHubConnectionString iotHubConnectionString, MqttTransportSettings settings, Func<IPAddress, int, Task<IChannel>> channelFactory)
            : base(settings)
        {
            this.mqttIotHubAdapterFactory = new MqttIotHubAdapterFactory(settings);
            this.messageQueue = new ConcurrentQueue<Message>();
            this.completionQueue = new Queue<string>();
            this.serverAddress = Dns.GetHostEntry(iotHubConnectionString.HostName).AddressList[0];
            this.qos = settings.PublishToServerQoS;
            this.eventLoopGroupKey = iotHubConnectionString.IotHubName + "#" + iotHubConnectionString.DeviceId + "#" + iotHubConnectionString.Audience;

            if (channelFactory == null)
            {
                switch (settings.GetTransportType())
                {
                    case TransportType.Mqtt_Tcp_Only:
                        this.channelFactory = this.CreateChannelFactory(iotHubConnectionString, settings);
                        break;
                    case TransportType.Mqtt_WebSocket_Only:
                        this.channelFactory = this.CreateWebSocketChannelFactory(iotHubConnectionString, settings);
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported Transport Setting {0}".FormatInvariant(settings.GetTransportType()));
                }
            }
            else
            {
                this.channelFactory = channelFactory;
            }

            this.closeRetryPolicy = new RetryPolicy(new TransientErrorIgnoreStrategy(), 5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Create a DeviceClient from individual parameters
        /// </summary>
        /// <param name="hostname">The fully-qualified DNS hostname of IoT Hub</param>
        /// <param name="authMethod">The authentication method that is used</param>
        /// <returns>DeviceClient</returns>
        public static MqttTransportHandler Create(string hostname, IAuthenticationMethod authMethod)
        {
            if (hostname == null)
            {
                throw new ArgumentNullException(nameof(hostname));
            }

            if (authMethod == null)
            {
                throw new ArgumentNullException(nameof(authMethod));
            }

            IotHubConnectionStringBuilder connectionStringBuilder = IotHubConnectionStringBuilder.Create(hostname, authMethod);
            return CreateFromConnectionString(connectionStringBuilder.ToString());
        }

        /// <summary>
        /// Create DeviceClient from the specified connection string
        /// </summary>
        /// <param name="connectionString">Connection string for the IoT hub</param>
        /// <returns>DeviceClient</returns>
        public static MqttTransportHandler CreateFromConnectionString(string connectionString)
        {
            if (connectionString == null)
            {
                throw new ArgumentNullException(nameof(connectionString));
            }

            IotHubConnectionString iotHubConnectionString = IotHubConnectionString.Parse(connectionString);

            return new MqttTransportHandler(iotHubConnectionString);
        }

        #region Client operations
        public override async Task OpenAsync(bool explicitOpen, CancellationToken cancellationToken)
        {
            this.EnsureValidState();

            if (this.State == TransportState.Open)
            {
                return;
            }

            await this.HandleTimeoutCancellation(this.OpenAsync, cancellationToken);
        }

        public override async Task SendEventAsync(Message message, CancellationToken cancellationToken)
        {
            this.EnsureValidState();

            await this.HandleTimeoutCancellation(() =>
            {
                if (this.channel == null && cancellationToken.IsCancellationRequested)
                {
                    return TaskConstants.Completed;
                }

                return this.channel.WriteAndFlushAsync(message);
            }, cancellationToken);
        }

        public override async Task SendEventAsync(IEnumerable<Message> messages, CancellationToken cancellationToken)
        {
            await this.HandleTimeoutCancellation(async () =>
            {
                foreach (Message message in messages)
                {
                    await this.SendEventAsync(message, cancellationToken);
                }
            }, cancellationToken);
        }

        public override async Task<Message> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Message message = null;

            await this.HandleTimeoutCancellation(async () =>
            {
                this.EnsureValidState();

                if (this.State != TransportState.Receiving)
                {
                    await this.SubscribeAsync();
                }

                bool hasMessage = await this.ReceiveMessageArrivalAsync(timeout, cancellationToken);

                if (hasMessage)
                {
                    lock (this.syncRoot)
                    {
                        this.messageQueue.TryDequeue(out message);
                        message.LockToken = message.LockToken;
                        if (this.qos == QualityOfService.AtLeastOnce)
                        {
                            this.completionQueue.Enqueue(message.LockToken);
                        }
                        message.LockToken = this.generationId + message.LockToken;
                    }
                }
            }, cancellationToken);

            return message;
        }

        private async Task<bool> ReceiveMessageArrivalAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            bool hasMessage = false;
            using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.disconnectAwaitersCancellationSource.Token))
            {
                hasMessage = await this.receivingSemaphore.WaitAsync(timeout, linkedCts.Token);
            }
            return hasMessage;
        }

        public override async Task CompleteAsync(string lockToken, CancellationToken cancellationToken)
        {
            this.EnsureValidState();

            if (this.qos == QualityOfService.AtMostOnce)
            {
                throw new IotHubClientTransientException("Complete is not allowed for QoS 0.");
            }

            await this.HandleTimeoutCancellation(async () =>
            {
                Task completeOperationCompletion;
                lock (this.syncRoot)
                {
                    if (!lockToken.StartsWith(this.generationId))
                    {
                        throw new IotHubClientTransientException("Lock token is stale or never existed. The message will be redelivered, please discard this lock token and do not retry operation.");
                    }

                    if (this.completionQueue.Count == 0)
                    {
                        throw new IotHubClientTransientException("Unknown lock token.");
                    }

                    string actualLockToken = this.completionQueue.Peek();
                    if (lockToken.IndexOf(actualLockToken, GenerationPrefixLength, StringComparison.Ordinal) != GenerationPrefixLength ||
                        lockToken.Length != actualLockToken.Length + GenerationPrefixLength)
                    {
                        throw new IotHubException($"Client MUST send PUBACK packets in the order in which the corresponding PUBLISH packets were received (QoS 1 messages) per [MQTT-4.6.0-2]. Expected lock token: '{actualLockToken}'; actual lock token: '{lockToken}'.");
                    }

                    this.completionQueue.Dequeue();
                    completeOperationCompletion = this.channel.WriteAndFlushAsync(actualLockToken);
                }
                await completeOperationCompletion;
            }, cancellationToken);
        }

        public override Task AbandonAsync(string lockToken, CancellationToken cancellationToken)
        {
            throw new IotHubException("MQTT protocol does not support this operation");
        }

        public override Task RejectAsync(string lockToken, CancellationToken cancellationToken)
        {
            throw new IotHubException("MQTT protocol does not support this operation");
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    if (this.TryStop())
                    {
                        this.Cleanup();
                    }
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        public override async Task CloseAsync()
        {
            if (this.TryStop())
            {
                await this.closeRetryPolicy.ExecuteAsync(this.CleanupAsync);
            }
            else
            {
                if (this.State == TransportState.Error)
                {
                    throw new IotHubClientException(this.fatalException);
                }
            }
        }

        #endregion

        #region MQTT callbacks
        void OnConnected()
        {
            if (this.TryStateTransition(TransportState.Opening, TransportState.Open))
            {
                this.connectCompletion.TryComplete();
            }
        }

        void HandleIncomingTwinResponse(Message message)
        {
            // BKTODO
        }

        void HandleIncomingTwinPatch(Message message)
        {
            // BKTODO
        }

        void HandleIncomingMethodPost(Message message)
        {
            // TODO: Haitham, this is where you put the code to build the MethodRequest object and call teh MethodCall handler 
            string[] tokens = System.Text.RegularExpressions.Regex.Split(message.MqttTopicName, "/");

            var mr = new MethodRequestInternal(tokens[3], tokens[4].Substring(6), message.BodyStream);
            this.messageListener(mr);
        }

        void OnMessageReceived(Message message)
        {
            if ((this.State & TransportState.Open) > 0)
            {
                if (message.MqttTopicName.StartsWith(twinResponseTopicPrefix))
                {
                    HandleIncomingTwinResponse(message);
                }
                else if (message.MqttTopicName.StartsWith(twinPatchTopicFilter))
                {
                    HandleIncomingTwinPatch(message);
                }
                else if (message.MqttTopicName.StartsWith(methodPostTopicFilter))
                {
                    HandleIncomingMethodPost(message);
                }
                else
                {
                    this.messageQueue.Enqueue(message);
                }
                // BKTODO: what is this semaphor about?
                this.receivingSemaphore.Release();
            }
        }

        async void OnError(Exception exception)
        {
            try
            {
                TransportState previousState = this.MoveToStateIfPossible(TransportState.Error, TransportState.Closed);
                switch (previousState)
                {
                    case TransportState.Error:
                    case TransportState.Closed:
                        return;
                    case TransportState.NotInitialized:
                    case TransportState.Opening:
                        this.fatalException = exception;
                        this.connectCompletion.TrySetException(exception);
                        this.subscribeCompletionSource.TrySetException(exception);
                        break;
                    case TransportState.Open:
                    case TransportState.Subscribing:
                        this.fatalException = exception;
                        this.subscribeCompletionSource.TrySetException(exception);
                        break;
                    case TransportState.Receiving:
                        this.fatalException = exception;
                        this.disconnectAwaitersCancellationSource.Cancel();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                await this.closeRetryPolicy.ExecuteAsync(this.CleanupAsync);
            }
            catch (Exception ex) when (!ex.IsFatal())
            {

            }
        }

        TransportState MoveToStateIfPossible(TransportState destination, TransportState illegalStates)
        {
            TransportState previousState = this.State;
            do
            {
                if ((previousState & illegalStates) > 0)
                {
                    return previousState;
                }
                TransportState prevState;
                if ((prevState = (TransportState)Interlocked.CompareExchange(ref this.state, (int)destination, (int)previousState)) == previousState)
                {
                    return prevState;
                }
                previousState = prevState;
            }
            while (true);
        }

        #endregion

        async Task OpenAsync()
        {
            if (this.TryStateTransition(TransportState.NotInitialized, TransportState.Opening))
            {
                try
                {
                    this.channel = await this.channelFactory(this.serverAddress, ProtocolGatewayPort);
                }
                catch (Exception ex) when (!ex.IsFatal())
                {
                    this.OnError(ex);
                    throw;
                }

                this.ScheduleCleanup(async () =>
                {
                    this.disconnectAwaitersCancellationSource.Cancel();
                    if (this.channel == null)
                    {
                        return;
                    }
                    if (this.channel.Active)
                    {
                        await this.channel.WriteAsync(DisconnectPacket.Instance);
                    }
                    if (this.channel.Open)
                    {
                        await this.channel.CloseAsync();
                    }
                });
            }

            await this.connectCompletion.Task;
        }

        bool TryStop()
        {
            TransportState previousState = this.MoveToStateIfPossible(TransportState.Closed, TransportState.Error);
            switch (previousState)
            {
                case TransportState.Closed:
                case TransportState.Error:
                    return false;
                case TransportState.NotInitialized:
                case TransportState.Opening:
                    this.connectCompletion.TrySetCanceled();
                    break;
                case TransportState.Open:
                case TransportState.Subscribing:
                    this.subscribeCompletionSource.TrySetCanceled();
                    break;
                case TransportState.Receiving:
                    this.disconnectAwaitersCancellationSource.Cancel();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            return true;
        }

        async Task SubscribeAsync()
        {
            if (this.TryStateTransition(TransportState.Open, TransportState.Subscribing))
            {
                await this.channel.WriteAsync(new SubscribePacket());
                if (this.TryStateTransition(TransportState.Subscribing, TransportState.Receiving))
                {
                    if (this.subscribeCompletionSource.TryComplete())
                    {
                        return;
                    }
                }
            }
            await this.subscribeCompletionSource.Task;
        }

        public override async Task EnableMethodsAsync(CancellationToken cancellationToken)
        {
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_001:  `EnableMethodsAsync` shall subscribe using the '$iothub/methods/POST/' topic filter. 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_002:  `EnableMethodsAsync` shall wait for a response to the subscription request. 
            // BKTODO: Codes_SRS_CSHARP_MQTT_TRANSPORT_18_003:  `EnableMethodsAsync` shall return failure if the subscription request fails. 
            await this.channel.WriteAsync(new SubscribePacket(0, new SubscriptionRequest(methodPostTopicFilter, QualityOfService.AtLeastOnce)));
        }

        public override Task DisableMethodsAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public override async Task SendMethodResponseAsync(MethodResponseInternal methodResponse, CancellationToken ct)
        {
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_005:  `SendMethodResponseAsync` shall allocate a `Message` object containing the method response. 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_006:  `SendMethodResponseAsync` shall set the message topic to '$iothub/methods/res/<STATUS>/?$rid=<REQUEST_ID>' where STATUS is the return status for the method and REQUEST_ID is the request ID received from the service in the original method call. 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_007:  `SendMethodResponseAsync` shall set the message body to the response payload of the `Method` object. 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_008:  `SendMethodResponseAsync` shall send the message to the service. 
            var message = new Message(methodResponse.BodyStream);

            message.MqttTopicName = methodResponseTopic.FormatInvariant(methodResponse.Status, methodResponse.RequestId);

            await this.SendEventAsync(message, ct);
        }

        public override async Task EnableTwinAsync(CancellationToken cancellationToken)
        {
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_009:  `EnableTwinAsync` shall subscribe using the '$iothub/twin/res/#' topic filter. 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_010:  `EnableTwinAsync` shall subscribe using the '$iothub/twin/PATCH/properties/desired/#' topic filter. 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_011:  `EnableTwinAsync` shall wait for responses on both subscriptions. 
            // BKTODO: Codes_SRS_CSHARP_MQTT_TRANSPORT_18_012:  If either subscription request fails, `EnableTwinAsync` shall return failure 
            Task[] tasks = {
                this.channel.WriteAsync(new SubscribePacket(0, new SubscriptionRequest(twinPatchTopicFilter, QualityOfService.AtLeastOnce))),
                this.channel.WriteAsync(new SubscribePacket(0, new SubscriptionRequest(twinResponseTopicFilter, QualityOfService.AtLeastOnce)))
            };
            await Task.WhenAll(tasks);
        }

        public override async Task SendTwinGetAsync(Twin twin, CancellationToken ct)
        {
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_014:  `SendTwinGetAsync` shall allocate a `Message` object to hold the `GET` request 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_015:  `SendTwinGetAsync` shall generate a GUID to use as the $rid property on the request 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_016:  `SendTwinGetAsync` shall set the `Message` topic to '$iothub/twin/GET/?$rid=<REQUEST_ID>' where REQUEST_ID is the GUID that was generated 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_017:  `SendTwinGetAsync` shall wait for a response from the service with a matching $rid value 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_018:  When a response is received, `SendTwinGetAsync` shall send it to the caller using the `TwinUpdateHandler`. 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_019:  If the response is failed, `SendTwinGetAsync` shall return that failure to the caller. 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_020:  If the response doesn't arrive within `MqttTransportHandler.TwinTimeout`, `SendTwinGetAsync` shall fail with a timeout error 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_021:  If the response contains a success code, `SendTwinGetAsync` shall return success to the caller  
            throw new NotImplementedException();
        }

        public override async Task SendTwinUpdateAsync(Twin twin, TwinProperties properties, CancellationToken ct)
        {
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_022:  `SendTwinUpdateAsync` shall allocate a `Message` object to hold the update request 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_023:  `SendTwinUpdateAsync` shall generate a GUID to use as the $rid property on the request 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_024:  `SendTwinUpdateAsync` shall set the `Message` topic to '$iothub/twin/PATCH/properties/reported/?$rid=<REQUEST_ID>' where REQUEST_ID is the GUID that was generated 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_025:  `SendTwinUpdateAsync` shall serialize the `properties` object into a JSON string 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_026:  `SendTwinUpdateAsync` shall set the body of the message to the JSON string 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_027:  `SendTwinUpdateAsync` shall wait for a response from the service with a matching $rid value 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_028:  If the response is failed, `SendTwinUpdateAsync` shall return that failure to the caller. 
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_029:  If the response doesn't arrive within `MqttTransportHandler.TwinTimeout`, `SendTwinUpdateAsync` shall fail with a timeout error.  
            // Codes_SRS_CSHARP_MQTT_TRANSPORT_18_030:  If the response contains a success code, `SendTwinUpdateAsync` shall return success to the caller. 
            throw new NotImplementedException();
        }

        Func<IPAddress, int, Task<IChannel>> CreateChannelFactory(IotHubConnectionString iotHubConnectionString, MqttTransportSettings settings)
        {
            return (address, port) =>
            {
                IEventLoopGroup eventLoopGroup = EventLoopGroupPool.TakeOrAdd(this.eventLoopGroupKey);

                Func<Stream, SslStream> streamFactory = stream => new SslStream(stream, true, settings.RemoteCertificateValidationCallback);
                var clientTlsSettings = settings.ClientCertificate != null ? 
                    new ClientTlsSettings(iotHubConnectionString.HostName, new List<X509Certificate> { settings.ClientCertificate }) : 
                    new ClientTlsSettings(iotHubConnectionString.HostName);
                Bootstrap bootstrap = new Bootstrap()
                    .Group(eventLoopGroup)
                    .Channel<TcpSocketChannel>()
                    .Option(ChannelOption.TcpNodelay, true)
                    .Option(ChannelOption.Allocator, UnpooledByteBufferAllocator.Default)
                    .Handler(new ActionChannelInitializer<ISocketChannel>(ch =>
                    {
                        var tlsHandler = new TlsHandler(streamFactory, clientTlsSettings);

                        ch.Pipeline
                            .AddLast(
                                tlsHandler, 
                                MqttEncoder.Instance, 
                                new MqttDecoder(false, MaxMessageSize), 
                                this.mqttIotHubAdapterFactory.Create(this.OnConnected, this.OnMessageReceived, this.OnError, iotHubConnectionString, settings));
                    }));

                this.ScheduleCleanup(() =>
                {
                    EventLoopGroupPool.Release(this.eventLoopGroupKey);
                    return TaskConstants.Completed;
                });

                return bootstrap.ConnectAsync(address, port);
            };
        }

        Func<IPAddress, int, Task<IChannel>> CreateWebSocketChannelFactory(IotHubConnectionString iotHubConnectionString, MqttTransportSettings settings)
        {
            return async (address, port) =>
            {
                IEventLoopGroup eventLoopGroup = EventLoopGroupPool.TakeOrAdd(this.eventLoopGroupKey);

                var websocketUri = new Uri(WebSocketConstants.Scheme + iotHubConnectionString.HostName + ":" + WebSocketConstants.SecurePort + WebSocketConstants.UriSuffix);
                var websocket = new ClientWebSocket();
                websocket.Options.AddSubProtocol(WebSocketConstants.SubProtocols.Mqtt);

                // Check if we're configured to use a proxy server
                IWebProxy webProxy = WebRequest.DefaultWebProxy;
                Uri proxyAddress = webProxy?.GetProxy(websocketUri);
                if (!websocketUri.Equals(proxyAddress))
                {
                    // Configure proxy server
                    websocket.Options.Proxy = webProxy;
                }

                if (settings.ClientCertificate != null)
                {
                    websocket.Options.ClientCertificates.Add(settings.ClientCertificate);
                }
                else
                {
                    websocket.Options.UseDefaultCredentials = true;
                }

                using (var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
                {
                    await websocket.ConnectAsync(websocketUri, cancellationTokenSource.Token);
                }

                var clientChannel = new ClientWebSocketChannel(null, websocket);
                clientChannel
                    .Option(ChannelOption.Allocator, UnpooledByteBufferAllocator.Default)
                    .Option(ChannelOption.AutoRead, false)
                    .Option(ChannelOption.RcvbufAllocator, new AdaptiveRecvByteBufAllocator())
                    .Option(ChannelOption.MessageSizeEstimator, DefaultMessageSizeEstimator.Default)
                    .Pipeline.AddLast(
                        MqttEncoder.Instance,
                        new MqttDecoder(false, MaxMessageSize),
                        this.mqttIotHubAdapterFactory.Create(this.OnConnected, this.OnMessageReceived, this.OnError, iotHubConnectionString, settings));
                await eventLoopGroup.GetNext().RegisterAsync(clientChannel);

                this.ScheduleCleanup(() =>
                {
                    EventLoopGroupPool.Release(this.eventLoopGroupKey);
                    return TaskConstants.Completed;
                });

                return clientChannel;
            };
        }

        void ScheduleCleanup(Func<Task> cleanupTask)
        {
            Func<Task> currentCleanupFunc = this.cleanupFunc;
            this.cleanupFunc = async () =>
            {
                await cleanupTask();

                if (currentCleanupFunc != null)
                {
                    await currentCleanupFunc();
                }
            };
        }

        async void Cleanup()
        {
            try
            {
                await this.closeRetryPolicy.ExecuteAsync(this.CleanupAsync);
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
            }
        }

        Task CleanupAsync()
        {
            if (this.cleanupFunc != null)
            {
                return this.cleanupFunc();
            }
            return TaskConstants.Completed;
        }

        bool TryStateTransition(TransportState fromState, TransportState toState)
        {
            return (TransportState)Interlocked.CompareExchange(ref this.state, (int)toState, (int)fromState) == fromState;
        }

        void EnsureValidState()
        {
            if (this.State == TransportState.Error)
            {
                throw new IotHubClientException(this.fatalException);
            }
            if (this.State == TransportState.Closed)
            {
                throw new ObjectDisposedException(this.GetType().Name);
            }
        }
    }
}
