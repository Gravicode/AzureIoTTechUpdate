#r "Microsoft.WindowsAzure.Storage"
#r "Newtonsoft.Json"

using System;
using Newtonsoft.Json;

public static void Run(string myEventHubMessage, out string outputQueueItem, TraceWriter log)
{
    log.Info($"C# Event Hub trigger function processed a message: {myEventHubMessage}");
    outputQueueItem = "";
    if (!String.IsNullOrEmpty(myEventHubMessage))
    {
        var obj = JsonConvert.DeserializeObject<SensorData>(myEventHubMessage);
        var tweets = "";
        if (obj.temp > 31)
        {
            tweets = "Meuni hareudang kieu, pengen mandi euy..";
        }
        else
        {
            tweets = "Adem yeuh..";
        }
        outputQueueItem = tweets;
    }
}

public class SensorData
{
    public DateTime idtimezone { set; get; }
    public string deviceid { set; get; }
    public double temp { set; get; }
    public double light { set; get; }
    public string acceleration { set; get; }
}