using System;
using System.Collections;
using System.Threading;
using Microsoft.SPOT;
using Microsoft.SPOT.Presentation;
using Microsoft.SPOT.Presentation.Controls;
using Microsoft.SPOT.Presentation.Media;
using Microsoft.SPOT.Presentation.Shapes;
using Microsoft.SPOT.Touch;
using System.Net;
using Gadgeteer.Networking;
using GT = Gadgeteer;
using GTM = Gadgeteer.Modules;
using Gadgeteer.Modules.GHIElectronics;
using GHI.Processor;
using Microsoft.SPOT.Hardware;
using GHI.Glide;
using System.Text;
using GHI.Glide.Geom;
using Json.NETMF;
using GHI.Glide.UI;
using GHI.SQLite;
using Microsoft.SPOT.Net.NetworkInformation;
using System.IO;
using Microsoft.Azure.Devices.Client;

namespace System.Diagnostics
{
    public enum DebuggerBrowsableState
    {
        Collapsed,
        Never,
        RootHidden
    }
}
namespace LoraDataReceiver
{
    #region EventHub
    public class EventHubClient
    {
        public string _serviceNamespace { set; get; }
        public string _hubName { set; get; }
        public string _deviceName { set; get; }
        public string _url { set; get; }

        public string _sas { set; get; }

        public EventHubClient(string serviceNamespace, string eventhub, string deviceName,string sas)
        {
            // Assign event hub details
            _sas = sas;
            _serviceNamespace = serviceNamespace;
            _hubName = eventhub;
            _deviceName = deviceName;
            // Generate the url to the event hub
            _url = "http://" + _serviceNamespace + ".servicebus.windows.net/" + _hubName + "/Publishers/" + _deviceName;
            //Endpoint=sb://smartcityhub.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=//Ql2lDwfU+SAis6BLPmIyeCy+CfpFz2ETS2IVBRMjc=
            //  Note: As the FEZ Panda (.NET MF 4.1) does not support SSL I need to send this to the field gateway over HTTP
            //_url = "http://dev-vs2014ctp-ty074cpf.cloudapp.net/" + _serviceNamespace + "/" + _hubName + "/" + _deviceName;
        }
        public bool SendEvent(DeviceData sensorData)
        {
            var success = false;
            try
            {
                
                // Format the sensor data as json
                var eventData = JsonSerializer.SerializeObject(sensorData);

                Debug.Print("Sending event data: " + eventData);

                // Create an HTTP Web request.
                HttpWebRequest webReq = HttpWebRequest.Create(_url) as HttpWebRequest;
                
                // Add required headers
                webReq.Method = "POST";
                webReq.Headers.Add("Authorization", _sas);
                webReq.ContentType = "application/atom+xml;type=entry;charset=utf-8";
                webReq.ContentLength = eventData.Length;
                webReq.KeepAlive = true;
              
                using (var writer = new StreamWriter(webReq.GetRequestStream()))
                {
                    writer.Write(eventData);
                }

                webReq.Timeout = 3000; // 3 secs
                using (var response = webReq.GetResponse() as HttpWebResponse)
                {
                    Debug.Print("HttpWebResponse: " + response.StatusCode.ToString());
                    // Check status code
                    success = (response.StatusCode == HttpStatusCode.Created);
                }
            }
            catch (Exception ex)
            {
                Debug.Print(ex.ToString());
            }

            return success;
        }
    }
    #endregion
    public partial class Program
    {
        // String containing Hostname, Device Id & Device Key in one of the following formats:
        //  "HostName=<iothub_host_name>;DeviceId=<device_id>;SharedAccessKey=<device_key>"
        //  "HostName=<iothub_host_name>;CredentialType=SharedAccessSignature;DeviceId=<device_id>;SharedAccessSignature=SharedAccessSignature sr=<iot_host>/devices/<device_id>&sig=<token>&se=<expiry_time>";
        private const string DeviceConnectionString = "HostName=IoTHubFree.azure-devices.net;DeviceId=NodeSensor;SharedAccessKey=SkQEo+132WTUdVi7mkXKP9B6Dv5dBT0f+7jqBUz7PTE=";
        static DeviceClient deviceClient = null;
        static bool IsSending = false;
        static int Counter = 0;
        //lora init
        private static SimpleSerial _loraSerial;
        private static string[] _dataInLora;
        //lora reset pin
        static OutputPort _restPort = new OutputPort(GHI.Pins.FEZSpiderII.Socket11.Pin3, true);
        private static string rx;
        //UI
        GHI.Glide.UI.TextBlock txtTime = null;
        GHI.Glide.UI.DataGrid GvData = null;
        GHI.Glide.UI.Button BtnReset = null;
        GHI.Glide.Display.Window window = null;
        GHI.Glide.UI.TextBlock txtMessage = null;
        //database
        Database myDatabase = null;
        // This method is run when the mainboard is powered up or reset.   


        #region Azure IoT
        static void SendEvent(DeviceClient deviceClient,string Message)
        {
                Message eventMessage = new Message(Encoding.UTF8.GetBytes(Message));
                Debug.Print(DateTime.Now.ToLocalTime() + "> Sending message, Data: [" + Message + "]");
                deviceClient.SendEvent(eventMessage);
            
        }

        static void ReceiveCommands(DeviceClient deviceClient)
        {
            Debug.Print("Device waiting for commands from IoTHub...");
            Message receivedMessage;
            string messageData;

            while (true)
            {
                receivedMessage = deviceClient.Receive();

                if (receivedMessage != null)
                {
                    StringBuilder sb = new StringBuilder();

                    foreach (byte b in receivedMessage.GetBytes())
                    {
                        sb.Append((char)b);
                    }

                    messageData = sb.ToString();

                    // dispose string builder
                    sb = null;

                    Debug.Print(DateTime.Now.ToLocalTime() + "> Received message: " + messageData);

                    deviceClient.Complete(receivedMessage);
                }

                //  Note: In this sample, the polling interval is set to 
                //  10 seconds to enable you to see messages as they are sent.
                //  To enable an IoT solution to scale, you should extend this //  interval. For example, to scale to 1 million devices, set 
                //  the polling interval to 25 minutes.
                //  For further information, see
                //  https://azure.microsoft.com/documentation/articles/iot-hub-devguide/#messaging
                Thread.Sleep(10000);
            }
        }

        #endregion

        void ProgramStarted()
        {
            //setup wifi
            ConnectNetwork();
            deviceClient = DeviceClient.CreateFromConnectionString(DeviceConnectionString, TransportType.Http1);            
        
           
            //set display
            this.videoOut.SetDisplayConfiguration(VideoOut.Resolution.Rca800x600);
            //set glide
            window = GlideLoader.LoadWindow(Resources.GetString(Resources.StringResources.Form1));

            txtTime = (GHI.Glide.UI.TextBlock)window.GetChildByName("txtTime");
            GvData = (GHI.Glide.UI.DataGrid)window.GetChildByName("GvData");
            BtnReset = (GHI.Glide.UI.Button)window.GetChildByName("BtnReset");
            txtMessage = (GHI.Glide.UI.TextBlock)window.GetChildByName("TxtMessage");
            Glide.MainWindow = window;

            //setup grid
            //create grid column
            GvData.AddColumn(new DataGridColumn("Time", 200));
            GvData.AddColumn(new DataGridColumn("Temp", 200));
            GvData.AddColumn(new DataGridColumn("Humid", 200));
            GvData.AddColumn(new DataGridColumn("Light", 200));
            GvData.AddColumn(new DataGridColumn("Gas", 200));


            // Create a database in memory,
            // file system is possible however!
            myDatabase = new GHI.SQLite.Database();
            myDatabase.ExecuteNonQuery("CREATE Table Sensor" +
            " (Time TEXT, Temp DOUBLE,Humid DOUBLE,Light DOUBLE,Gas DOUBLE)");
            //reset database n display
            BtnReset.TapEvent += (object sender) =>
            {
                Counter = 0;
                myDatabase.ExecuteNonQuery("DELETE FROM Sensor");
                GvData.Clear();
                GvData.Invalidate();
            };

            //reset lora
            _restPort.Write(false);
            Thread.Sleep(1000);
            _restPort.Write(true);
            Thread.Sleep(1000);


            _loraSerial = new SimpleSerial(GHI.Pins.FEZSpiderII.Socket11.SerialPortName, 57600);
            _loraSerial.Open();
            _loraSerial.DataReceived += _loraSerial_DataReceived;
            //get version
            _loraSerial.WriteLine("sys get ver");
            Thread.Sleep(1000);
            //pause join
            _loraSerial.WriteLine("mac pause");
            Thread.Sleep(1500);
            //antena power
            _loraSerial.WriteLine("radio set pwr 14");
            Thread.Sleep(1500);
            //set device to receive
            _loraSerial.WriteLine("radio rx 0"); //set module to RX
            txtMessage.Text = "LORA-RN2483 setup has been completed...";
            txtMessage.Invalidate();
            window.Invalidate();
            //myDatabase.Dispose();

        }

      
        public void PrintLine(string Output, bool AddNewLine = true)
        {
            Debug.Print(Output);
        }
        #region Network
        public void ConnectNetwork()
        {
            string SSID = "wifi berbayar";
            string WifiKey = "123qweasd";
            //setup network
            wifiRS21.DebugPrintEnabled = true;
            NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged;
            //setup network
            wifiRS21.NetworkInterface.Open();
            wifiRS21.NetworkInterface.EnableDhcp();
            wifiRS21.NetworkInterface.EnableDynamicDns();

            GHI.Networking.WiFiRS9110.NetworkParameters[] info = wifiRS21.NetworkInterface.Scan(SSID);
            if (info != null)
            {
                wifiRS21.NetworkInterface.Join(SSID, WifiKey);
                PrintLine("Waiting for DHCP...");
                while (wifiRS21.NetworkInterface.IPAddress == "0.0.0.0")
                {
                    Thread.Sleep(250);
                }
                PrintLine("network joined");
                PrintLine("active network:" + SSID);
                Thread.Sleep(1000);

                ListNetworkInterfaces();
            }
            else
            {
                PrintLine("SSID cannot be found!");

            }
        }
        void ListNetworkInterfaces()
        {
            var settings = wifiRS21.NetworkSettings;

            PrintLine("------------------------------------------------");
            PrintLine("MAC: " + ByteExt.ToHexString(settings.PhysicalAddress, "-"));
            PrintLine("IP Address:   " + settings.IPAddress);
            PrintLine("DHCP Enabled: " + settings.IsDhcpEnabled);
            PrintLine("Subnet Mask:  " + settings.SubnetMask);
            PrintLine("Gateway:      " + settings.GatewayAddress);
            PrintLine("------------------------------------------------");

        }

        void ScanWifi()
        {
            // look for avaiable networks
            var scanResults = wifiRS21.NetworkInterface.Scan();

            // go through each network and print out settings in the debug window
            foreach (GHI.Networking.WiFiRS9110.NetworkParameters result in scanResults)
            {

                PrintLine("****" + result.Ssid + "****");
                //PrintLine("ChannelNumber = " + result.Channel);
                PrintLine("networkType = " + result.NetworkType);
                //PrintLine("PhysicalAddress = " + GetMACAddress(result.PhysicalAddress));
                //PrintLine("RSSI = " + result.Rssi);
                PrintLine("SecMode = " + result.SecurityMode);
            }

        }
        string GetMACAddress(byte[] PhysicalAddress)
        {
            return ByteToHex(PhysicalAddress[0]) + "-"
                                + ByteToHex(PhysicalAddress[1]) + "-"
                                + ByteToHex(PhysicalAddress[2]) + "-"
                                + ByteToHex(PhysicalAddress[3]) + "-"
                                + ByteToHex(PhysicalAddress[4]) + "-"
                                + ByteToHex(PhysicalAddress[5]);
        }
        string ByteToHex(byte number)
        {
            string hex = "0123456789ABCDEF";
            return new string(new char[] { hex[(number & 0xF0) >> 4], hex[number & 0x0F] });
        }
        private static void NetworkChange_NetworkAddressChanged(object sender, Microsoft.SPOT.EventArgs e)
        {
            Debug.Print("Network address changed");
        }

        private static void NetworkChange_NetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            Debug.Print("Network availability: " + e.IsAvailable.ToString());
        }
        #endregion

        //convert hex to string
        string HexStringToString(string hexString)
        {
            if (hexString == null || (hexString.Length & 1) == 1)
            {
                throw new ArgumentException();
            }
            var sb = new StringBuilder();
            for (var i = 0; i < hexString.Length; i += 2)
            {
                var hexChar = hexString.Substring(i, 2);
                sb.Append((char)Convert.ToByte(hexChar));
            }
            return sb.ToString();
        }
        //convert hex to ascii
        private string HexString2Ascii(string hexString)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i <= hexString.Length - 2; i += 2)
            {
                int x = Int32.Parse(hexString.Substring(i, 2));
                sb.Append(new string(new char[] { (char)x }));
            }
            return sb.ToString();
        }
        //lora data received
        void _loraSerial_DataReceived(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
        {
            _dataInLora = _loraSerial.Deserialize();
            for (int index = 0; index < _dataInLora.Length; index++)
            {
                rx = _dataInLora[index];
                //if error
                if (_dataInLora[index].Length > 7)
                {
                    if (rx.Substring(0, 9) == "radio_err")
                    {
                        Debug.Print("!!!!!!!!!!!!! Radio Error !!!!!!!!!!!!!!");
                        PrintToLCD("Radio Error");

                        _restPort.Write(false);
                        Thread.Sleep(1000);
                        _restPort.Write(true);
                        Thread.Sleep(1000);
                        _loraSerial.WriteLine("mac pause");
                        Thread.Sleep(1000);
                        _loraSerial.WriteLine("radio rx 0");
                        return;

                    }
                    //if receive data
                    if (rx.Substring(0, 8) == "radio_rx")
                    {
                        string hex = _dataInLora[index].Substring(10);

                        Mainboard.SetDebugLED(true);
                        Thread.Sleep(500);
                        Mainboard.SetDebugLED(false);

                        Debug.Print(hex);
                        Debug.Print(Unpack(hex));
                        //update display

                        PrintToLCD(Unpack(hex));
                        Thread.Sleep(100);
                        // set module to RX
                        _loraSerial.WriteLine("radio rx 0");
                    }
                }
            }

        }
        //extract hex to string
        public static string Unpack(string input)
        {
            byte[] b = new byte[input.Length / 2];

            for (int i = 0; i < input.Length; i += 2)
            {
                b[i / 2] = (byte)((FromHex(input[i]) << 4) | FromHex(input[i + 1]));
            }
            return new string(Encoding.UTF8.GetChars(b));
        }
        public static int FromHex(char digit)
        {
            if ('0' <= digit && digit <= '9')
            {
                return (int)(digit - '0');
            }

            if ('a' <= digit && digit <= 'f')
                return (int)(digit - 'a' + 10);

            if ('A' <= digit && digit <= 'F')
                return (int)(digit - 'A' + 10);

            throw new ArgumentException("digit");
        }
        void PrintToLCD(string message)
        {
            String[] origin_names = null;
            ArrayList tabledata = null;
            //cek message
            if (message != null && message.Length > 0)
            {
                try
                {

                    if (message == "Radio Error") return;
                    var obj = Json.NETMF.JsonSerializer.DeserializeString(message) as Hashtable;
                    var detail = obj["Data"] as Hashtable;
                    DeviceData data = new DeviceData() { DeviceSN = obj["DeviceSN"].ToString() };
                    data.Data = new DataSensor() { Gas = Convert.ToDouble(detail["Gas"].ToString()), Temp = Convert.ToDouble(detail["Temp"].ToString()), Humid = Convert.ToDouble(detail["Humid"].ToString()), Light = Convert.ToDouble(detail["Light"].ToString()) };
                    //update display
                    txtTime.Text = DateTime.Now.ToString("dd/MMM/yyyy HH:mm:ss");
                    txtMessage.Text = "Data Reveiced Successfully.";
                    txtTime.Invalidate();
                    txtMessage.Invalidate();

                    var TimeStr = DateTime.Now.ToString("dd/MM/yy HH:mm");
                    //insert to db
                    var item = new DataGridItem(new object[] { TimeStr, data.Data.Temp, data.Data.Humid, data.Data.Light, data.Data.Gas });
                    //add data to grid
                    GvData.AddItem(item);
                    Counter++;

                    GvData.Invalidate();
                    window.Invalidate();

                    //add rows to table
                    myDatabase.ExecuteNonQuery("INSERT INTO Sensor (Time, Temp,Humid,Light,Gas)" +
                    " VALUES ('" + TimeStr + "' , " + data.Data.Temp + ", " + data.Data.Humid + ", " + data.Data.Light + ", " + data.Data.Gas + ")");
                    window.Invalidate();
                    if (Counter > 13)
                    {
                        //reset
                        Counter = 0;
                        myDatabase.ExecuteNonQuery("DELETE FROM Sensor");
                        GvData.Clear();
                        GvData.Invalidate();
                    }
                    if (data != null)
                    {
                        if (!IsSending)
                        {
                            try
                            {
                                IsSending = true;
                                var eventData = JsonSerializer.SerializeObject(data);
                                Debug.Print("Sending event data: " + eventData);
                                SendEvent(deviceClient, eventData);
                            }
                            catch (Exception ex)
                            {
                                txtMessage.Text = message + "_" + ex.Message + "_" + ex.StackTrace;
                                txtMessage.Invalidate();
                            }
                            finally
                            {
                                IsSending = false;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    txtMessage.Text = message + "_" + ex.Message + "_" + ex.StackTrace;
                    txtMessage.Invalidate();
                }
            }

        }








    }

    public static class ByteExt
    {
        private static char[] _hexCharacterTable = new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'A', 'B', 'C', 'D', 'E', 'F' };

#if MF_FRAMEWORK_VERSION_V4_1
    public static string ToHexString(byte[] array, string delimiter = "-")
#else
        public static string ToHexString(this byte[] array, string delimiter = "-")
#endif
        {
            if (array.Length > 0)
            {
                // it's faster to concatenate inside a char array than to
                // use string concatenation
                char[] delimeterArray = delimiter.ToCharArray();
                char[] chars = new char[array.Length * 2 + delimeterArray.Length * (array.Length - 1)];

                int j = 0;
                for (int i = 0; i < array.Length; i++)
                {
                    chars[j++] = (char)_hexCharacterTable[(array[i] & 0xF0) >> 4];
                    chars[j++] = (char)_hexCharacterTable[array[i] & 0x0F];

                    if (i != array.Length - 1)
                    {
                        foreach (char c in delimeterArray)
                        {
                            chars[j++] = c;
                        }

                    }
                }

                return new string(chars);
            }
            else
            {
                return string.Empty;
            }
        }
    }


    #region Model Classes
    public class DeviceData
    {
        public string DeviceSN { set; get; }
        public DataSensor Data { set; get; }
    }

    public class DataSensor
    {
        public double Gas { set; get; }
        public double Temp { set; get; }
        public double Humid { set; get; }
        public double Light { set; get; }

    }
    #endregion
}
