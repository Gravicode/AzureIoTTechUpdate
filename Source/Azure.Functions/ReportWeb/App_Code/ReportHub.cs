using System;
using System.Web;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Hubs;
using System.Configuration;
using System.Net;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Linq;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.WindowsAzure.Storage;
using System.Threading.Tasks;

namespace ReportWeb
{
    public enum TipeLaporan
    {

        Jalanan = 1,
        Jembatan
    }
    [Serializable]
    public class Laporan : TableEntity
    {
        public void GenerateID()
        {
            this.PartitionKey = "XXX";
            this.RowKey = Guid.NewGuid().ToString();
        }
        public string noLaporan { set; get; }
        public DateTime tglLaporan { set; get; }
        public string nama { set; get; }
        public string alamat { set; get; }
        public string telpon { set; get; }
        public string email { set; get; }
        public string ktp { set; get; }
        public TipeLaporan tipeKerusakan { set; get; }
        public string keterangan { set; get; }
        public string lokasi { set; get; }
        public DateTime waktu { set; get; }
        public int skalaKerusakan { set; get; }
    }

    //class untuk penampung data sensor
   
    //signal R start up class
    [HubName("ReportHub")]
    public class ReportHub : Hub
    {
   
        public ReportHub()
        {
           
        }

        [HubMethodName("GetReport")]
        public async Task<List<Laporan>> GetReport()
        {
            string ConnStr = ConfigurationManager.AppSettings["ConnStr"];

            // Retrieve the storage account from the connection string.
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(ConnStr);

            // Create the table client.
            CloudTableClient tableClient = storageAccount.CreateCloudTableClient();

            // Create the CloudTable object that represents the "people" table.
            CloudTable table = tableClient.GetTableReference("laporan");

            // Construct the query operation for all customer entities where PartitionKey="Smith".
            TableQuery<Laporan> query = new TableQuery<Laporan>().Take(10);

            var datas = new List<Laporan>();
            // Print the fields for each customer.
            TableContinuationToken token = null;

            do
            {
                var seg = await table.ExecuteQuerySegmentedAsync(query, token);
                token = seg.ContinuationToken;
                foreach (var entity in seg.Results)
                {
                     datas.Add(entity);
                }

            }
            while (token != null);
            return await Task.FromResult(datas);

        }
      
    }
}