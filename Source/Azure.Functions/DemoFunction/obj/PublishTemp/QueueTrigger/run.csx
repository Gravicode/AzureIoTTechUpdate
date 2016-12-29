#r "System.Net.Http"
#r "Microsoft.WindowsAzure.Storage"

using System.Net;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;

using Microsoft.Azure; // Namespace for CloudConfigurationManager
using Microsoft.WindowsAzure.Storage; // Namespace for CloudStorageAccount
using Microsoft.WindowsAzure.Storage.Table;


public static async void Run(string myQueueItem, TraceWriter log)
{
    log.Info($"C# Queue trigger function processed: {myQueueItem}");
    var y = await SentimentService.GetSentiment(myQueueItem);
    if (y != null)
    {
        var sentiment = (y.documents[0].score < 0.5 ? "negative message" : "positive message");
        var speaknow = $"{myQueueItem} => this is a " + sentiment;
        log.Info(speaknow);
        //
        // Retrieve the storage account from the connection string.
        CloudStorageAccount storageAccount = CloudStorageAccount.Parse(
            Environment.GetEnvironmentVariable("STORAGE_KEY"));

        // Create the table client.
        CloudTableClient tableClient = storageAccount.CreateCloudTableClient();

        // Create the CloudTable object that represents the "people" table.
        CloudTable table = tableClient.GetTableReference("tweets");
        // Create the table if it doesn't exist.
        table.CreateIfNotExists();
        // Create a new customer entity.
        Tweets tweet1 = new Tweets();
        tweet1.Sentiment = sentiment;
        tweet1.Message = myQueueItem;

        // Create the TableOperation object that inserts the customer entity.
        TableOperation insertOperation = TableOperation.Insert(tweet1);

        // Execute the insert operation.
        table.Execute(insertOperation);
        log.Info("data inserted to table.");
    }
}

public class Tweets : TableEntity
{


    public Tweets()
    {
        this.PartitionKey = "AAA";
        this.RowKey = Guid.NewGuid().ToString();
    }

    public string Sentiment { get; set; }

    public string Message { get; set; }
}

public class Document
{
    public double score { get; set; }
    public string id { get; set; }
}

public class SentimentResponse
{
    public List<Document> documents { get; set; }
    public List<object> errors { get; set; }
}

public class SentimentService
{
    /// <summary>
    /// Azure portal URL.
    /// </summary>
    private const string BaseUrl = "https://westus.api.cognitive.microsoft.com/";

    /// <summary>
    /// Your account key goes here.
    /// </summary>


    /// <summary>
    /// Maximum number of languages to return in language detection API.
    /// </summary>
    private const int NumLanguages = 1;

    public const string TEXTANALYSIS_KEY = "a3b5cae52b4e4c9da87923a053d24b9c";

    public static async Task<SentimentResponse> GetSentiment(string Message)
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.BaseAddress = new Uri(BaseUrl);

                // Request headers.
                client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", TEXTANALYSIS_KEY);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // Request body. Insert your text data here in JSON format.
                byte[] byteData = Encoding.UTF8.GetBytes("{\"documents\":[" +
                    "{\"id\":\"1\",\"text\":\"" + Message + "\"}]}");
                /*
                // Detect key phrases:
                var uri = "text/analytics/v2.0/keyPhrases";
                var response = await CallEndpoint(client, uri, byteData);
                Debug.WriteLine("\nDetect key phrases response:\n" + response);

                // Detect language:
                var queryString = HttpUtility.ParseQueryString(string.Empty);
                queryString["numberOfLanguagesToDetect"] = NumLanguages.ToString(CultureInfo.InvariantCulture);
                uri = "text/analytics/v2.0/languages?" + queryString;
                response = await CallEndpoint(client, uri, byteData);
                Debug.WriteLine("\nDetect language response:\n" + response);
                */
                // Detect sentiment:
                var uri = "text/analytics/v2.0/sentiment";
                var response = await CallEndpoint(client, uri, byteData);
                var item = JsonConvert.DeserializeObject<SentimentResponse>(response);
                Debug.WriteLine("\nDetect sentiment response:\n" + response);
                return item;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            return null;
        }
    }

    static async Task<String> CallEndpoint(HttpClient client, string uri, byte[] byteData)
    {
        using (var content = new ByteArrayContent(byteData))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var response = await client.PostAsync(uri, content);
            return await response.Content.ReadAsStringAsync();
        }
    }
}