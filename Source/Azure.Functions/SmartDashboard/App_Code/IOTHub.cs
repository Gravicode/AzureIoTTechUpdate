using System;
using System.Web;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Hubs;
using System.Configuration;
using System.Net;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json;
using ServiceStack.Redis;
using System.Linq;
using MoreLinq;
using Microsoft.Azure.Devices.Client;
using Microsoft.ServiceBus.Messaging;
using System.Threading;
using System.Threading.Tasks;

namespace IOT.Web
{
    //class untuk bikin chart
    public class DataSeries
    {
        public string name { set; get; }
        public List<double> data { set; get; }
    }

    //class untuk penampung data sensor
    [Serializable]

    public class SensorData
    {
        public DateTime Created { set; get; }
        public string DeviceId { set; get; }
        public double Temp { set; get; }
        public double Light { set; get; }
        public string Accel { set; get; }
    }
    //signal R start up class
    [HubName("IOTHub")]
    public class IOTHub : Hub
    {
        static string connectionString = "HostName=IoTHubFree.azure-devices.net;SharedAccessKeyName=iothubowner;SharedAccessKey=LLRAMqXsU8/2Vs88/Z7nM2L0L0xRGxvhia4yh0VLgnE=";
        static string iotHubD2cEndpoint = "messages/events";
        static EventHubClient eventHubClient;
        private static async Task ReceiveMessagesFromDeviceAsync(string partition, CancellationToken ct)
        {
            var eventHubReceiver = eventHubClient.GetDefaultConsumerGroup().CreateReceiver(partition, DateTime.UtcNow);
            while (true)
            {
                if (ct.IsCancellationRequested) break;
                EventData eventData = await eventHubReceiver.ReceiveAsync();
                if (eventData == null) continue;

                string Pesan = Encoding.UTF8.GetString(eventData.GetBytes());
                var SensorData = JsonConvert.DeserializeObject<SensorData>(Pesan);
                SensorData.Created = DateTime.Now;
                var datas = InsertData(SensorData);
                //update tampilan chart realtime
                UpdateChart(datas);
                //WriteMessage(Pesan);
                Console.WriteLine("Message received. Partition: {0} Data: '{1}'", partition, Pesan);
            }
        }
    

        static List<SensorData> InsertData(SensorData node)
        {
            //isi tanggal saat ini ke data sensor
            node.Created = DateTime.Now;
            var data = new List<SensorData>();
            //init redis client
            using (var redisManager = new PooledRedisClientManager())
            using (var redis = redisManager.GetClient())
            {
                var redisSensorDatas = redis.As<SensorData>();
                //masukan data ke redis                
                redisSensorDatas.Store(node);
                //ambil data sensor 10 terakhir
                var temp = redisSensorDatas.GetAll().ToList();
                data = (from c in temp
                        orderby c.Created ascending
                       select c).TakeLast(10).ToList();

            }
            //kirim ke web untuk di render
            return data;
        }
        public IOTHub()
        {
            Console.WriteLine("Receive messages. Ctrl-C to exit.\n");
            eventHubClient = EventHubClient.CreateFromConnectionString(connectionString, iotHubD2cEndpoint);

            var d2cPartitions = eventHubClient.GetRuntimeInformation().PartitionIds;

            CancellationTokenSource cts = new CancellationTokenSource();

            System.Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("Exiting...");
            };

            var tasks = new List<Task>();
            foreach (string partition in d2cPartitions)
            {
                tasks.Add(ReceiveMessagesFromDeviceAsync(partition, cts.Token));
            }
            Task.WaitAll(tasks.ToArray());
        }

     
        internal static void UpdateChart(List<SensorData> SensorDatas)
        {
            //populate data chart di halaman web
            var datas = new List<DataSeries>();
            var context = GlobalHost.ConnectionManager.GetHubContext<IOTHub>();
            var timeseries = from c in SensorDatas
                             select c.Created.ToString("HH:mm:ss");
            var tempseries = from c in SensorDatas
                             select c.Temp;
            datas.Add(new DataSeries() { name = "temperatur", data = tempseries.ToList() });
            dynamic allClients = context.Clients.All.UpdateChart("Temperatur","div_temp",timeseries, datas);
           
            var lightseries = from c in SensorDatas
                              select c.Light;
            datas.Clear();
            datas.Add(new DataSeries() { name = "cahaya", data = lightseries.ToList() });
            allClients = context.Clients.All.UpdateChart("Cahaya","div_light",timeseries, datas);
        }
        //fungsi log data ke halaman web 
        internal static void WriteMessage(string message)
        {
            var context = GlobalHost.ConnectionManager.GetHubContext<IOTHub>();
            dynamic allClients = context.Clients.All.WriteData(message);
        }
        
    }
}