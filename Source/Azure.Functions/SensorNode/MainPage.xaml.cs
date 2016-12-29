using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Microsoft.Azure.Devices.Client;
using Newtonsoft.Json;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409
using GIS = GHIElectronics.UWP.Shields;
using System.Text;

namespace SensorNode
{
    public class SensorData
    {
        public string DeviceId { set; get; }
        public double Temp { set; get; }
        public double Light { set; get; }
        public string Accel { set; get; }
    }
    public sealed partial class MainPage : Page
    {
        static DeviceClient deviceClient;
        static string iotHubUri = "IoTHubFree.azure-devices.net";
        static string deviceKey = "SkQEo+132WTUdVi7mkXKP9B6Dv5dBT0f+7jqBUz7PTE=";
        private GIS.FEZHAT hat;
        private DispatcherTimer timer;
        private bool next;
        private int i;
        private static async void SendTelemetry(string MessageStr)
        {
                var message = new Message(Encoding.ASCII.GetBytes(MessageStr));
                await deviceClient.SendEventAsync(message);
        }
        public MainPage()
        {
            this.InitializeComponent();

            this.Setup();
        }

        private async void Setup()
        {
            deviceClient = DeviceClient.Create(iotHubUri, new DeviceAuthenticationWithRegistrySymmetricKey("NodeSensor", deviceKey), TransportType.Amqp);
            
            this.hat = await GIS.FEZHAT.CreateAsync();

            this.hat.S1.SetLimits(500, 2400, 0, 180);
            this.hat.S2.SetLimits(500, 2400, 0, 180);
            
            this.timer = new DispatcherTimer();
            this.timer.Interval = TimeSpan.FromMilliseconds(3000);
            this.timer.Tick += this.OnTick;
            this.timer.Start();
        }

        private void OnTick(object sender, object e)
        {
            double x, y, z;

            this.hat.GetAcceleration(out x, out y, out z);

            SensorData data = new SensorData();
            data.DeviceId = "device001";
            data.Light = this.hat.GetLightLevel();
            data.Temp = this.hat.GetTemperature();
            data.Accel = $"({x:N2}, {y:N2}, {z:N2})";
            var msg = JsonConvert.SerializeObject(data);
            SendTelemetry(msg);
            StatusTxt.Text = $"Data Sent at {DateTime.Now.ToString("dd/MM/yy HH:mm:ss")}";
            this.LightTextBox.Text = this.hat.GetLightLevel().ToString("P2");
            this.TempTextBox.Text = this.hat.GetTemperature().ToString("N2");

            this.AccelTextBox.Text = $"({x:N2}, {y:N2}, {z:N2})";
            this.Button18TextBox.Text = this.hat.IsDIO18Pressed().ToString();
            this.Button22TextBox.Text = this.hat.IsDIO22Pressed().ToString();
            this.AnalogTextBox.Text = this.hat.ReadAnalog(GIS.FEZHAT.AnalogPin.Ain1).ToString("N2");
            
            if ((this.i++ % 5) == 0)
            {
                this.LedsTextBox.Text = this.next.ToString();

                this.hat.DIO24On = this.next;
                this.hat.D2.Color = this.next ? GIS.FEZHAT.Color.White : GIS.FEZHAT.Color.Black;
                this.hat.D3.Color = this.next ? GIS.FEZHAT.Color.White : GIS.FEZHAT.Color.Black;

                this.hat.WriteDigital(GIS.FEZHAT.DigitalPin.DIO16, this.next);
                this.hat.WriteDigital(GIS.FEZHAT.DigitalPin.DIO26, this.next);

                this.hat.SetPwmDutyCycle(GIS.FEZHAT.PwmPin.Pwm5, this.next ? 1.0 : 0.0);
                this.hat.SetPwmDutyCycle(GIS.FEZHAT.PwmPin.Pwm6, this.next ? 1.0 : 0.0);
                this.hat.SetPwmDutyCycle(GIS.FEZHAT.PwmPin.Pwm7, this.next ? 1.0 : 0.0);
                this.hat.SetPwmDutyCycle(GIS.FEZHAT.PwmPin.Pwm11, this.next ? 1.0 : 0.0);
                this.hat.SetPwmDutyCycle(GIS.FEZHAT.PwmPin.Pwm12, this.next ? 1.0 : 0.0);

                this.next = !this.next;
            }
            /*
            if (this.hat.IsDIO18Pressed())
            {
                this.hat.S1.Position += 5.0;
                this.hat.S2.Position += 5.0;

                if (this.hat.S1.Position >= 180.0)
                {
                    this.hat.S1.Position = 0.0;
                    this.hat.S2.Position = 0.0;
                }
            }

            if (this.hat.IsDIO22Pressed())
            {
                if (this.hat.MotorA.Speed == 0.0)
                {
                    this.hat.MotorA.Speed = 0.5;
                    this.hat.MotorB.Speed = -0.7;
                }
            }
            else
            {
                if (this.hat.MotorA.Speed != 0.0)
                {
                    this.hat.MotorA.Speed = 0.0;
                    this.hat.MotorB.Speed = 0.0;
                }
            }*/
        }
    }
}