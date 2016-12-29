using Microsoft.Bot.Builder.FormFlow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Connector;
using System.Threading.Tasks;

using Microsoft.Bot.Builder.FormFlow.Advanced;
using Microsoft.Bot.Builder.Resource;
using System.Resources;
using System.Text;
using System.Threading;
using Microsoft.Azure; // Namespace for CloudConfigurationManager
using Microsoft.WindowsAzure.Storage; // Namespace for CloudStorageAccount
using Microsoft.WindowsAzure.Storage.Queue; // Namespace for Queue storage types
using System.Configuration;
using Newtonsoft.Json;

namespace LaporBot
{
    public enum TipeLaporan
    {
        [Terms("jalan", "jalanan")]
        Jalanan =1,
        Jembatan
    }
    [Serializable]
    public class Laporan
    {
        public string NoLaporan;
        public DateTime TglLaporan;
        [Prompt("Siapa nama Anda ? {||}")]
        public string Nama;
        [Prompt("Boleh minta alamatnya ? {||}")]
        public string Alamat;
        [Prompt("Berapa No. telponnya ? {||}")]
        public string Telpon;
        [Prompt("Alamat E-mail Anda ? {||}")]
        public string Email;
        [Prompt("Isi No. KTP-nya ? {||}")]
        public string KTP;
        [Prompt("Kerusakan apa yang ingin Anda laporkan ? {||}")]
        public TipeLaporan TipeKerusakan;
        [Prompt("Silakan masukan keterangan / laporan Anda.. {||}")]
        public string Keterangan;
        [Prompt("Dimana lokasinya ? {||}")]
        public string Lokasi;
        [Prompt("Kapan Anda lihat ? {||}")]
        public DateTime Waktu;

        [Prompt("Masukan perkiraan skala kerusakan, 1 [ringan] - 10 [berat] ? ")]
        public int SkalaKerusakan = 1;

        public static IForm<Laporan> BuildForm()
        {

            OnCompletionAsyncDelegate<Laporan> processReport = async (context, state) =>
            {
                await Task.Run(() =>
                {
                    state.NoLaporan = $"LP-{DateTime.Now.ToString("dd_MM_yyyy_HH_mm_ss")}";
                    state.TglLaporan = DateTime.Now;
                    // Retrieve storage account from connection string.
                    CloudStorageAccount storageAccount = CloudStorageAccount.Parse(
                        ConfigurationManager.AppSettings["StorageConnectionString"]);

                    // Create the queue client.
                    CloudQueueClient queueClient = storageAccount.CreateCloudQueueClient();

                    // Retrieve a reference to a queue.
                    CloudQueue queue = queueClient.GetQueueReference("laporan");

                    // Create the queue if it doesn't already exist.
                    queue.CreateIfNotExists();

                    // Create a message and add it to the queue.
                    CloudQueueMessage message = new CloudQueueMessage(JsonConvert.SerializeObject(state));
                    queue.AddMessage(message);
                    Console.WriteLine("Push data ke que");
                }
                );
                
            };
            var builder = new FormBuilder<Laporan>(false);
            var form = builder
                    .Message("Selamat datang di pelaporan kerusakan PU.")
                        .Field(nameof(Nama))
                        .Field(nameof(Alamat))
                        .Field(nameof(Telpon))
                        .Field(nameof(Email))
                        .Field(nameof(KTP))
                        .Field(nameof(TipeKerusakan))
                        .Field(nameof(Keterangan))
                        .Field(nameof(TipeKerusakan))
                        .Field(nameof(Lokasi))
                        .Field(nameof(Waktu))
                        .Field(nameof(SkalaKerusakan), validate:
                            async (state, value) =>
                            {
                                var result = new ValidateResult { IsValid = true, Value = value, Feedback = "ok, skala valid" };
                                var jml = int.Parse(value.ToString());
                                if (jml <= 0)
                                {
                                    result.Feedback = "Isilah dengan serius, kerusakan ringan minimal nilainya 1";
                                    result.IsValid = false;
                                }
                                else if (jml > 10)
                                {
                                    result.Feedback = "Jangan main-main dunk, skala kerusakan terberat itu 10";
                                    result.IsValid = false;
                                }
                                return result;
                            })
                        .Confirm(async (state) =>
                        {
                            var pesan = $"Laporan dari {state.Nama} tentang {state.TipeKerusakan.ToString()} sudah kami terima, apakah data ini sudah valid ?";
                            return new PromptAttribute(pesan);
                        })
                        .Message($"Terima kasih atas laporannya.")
                        .OnCompletion(processReport)
                        .Build();
            return form;
        }
    }
}