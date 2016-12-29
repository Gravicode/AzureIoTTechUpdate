#r "Newtonsoft.Json"
#r "Microsoft.WindowsAzure.Storage"

using Newtonsoft.Json;
using Microsoft.WindowsAzure.Storage.Table;
using System;

public async static void Run(string myQueueItem, IAsyncCollector<Laporan> outTable, TraceWriter log)
{
    Laporan lap = JsonConvert.DeserializeObject<Laporan>(myQueueItem);
    lap.GenerateID();
    log.Info($"Laporan diterima: {lap.noLaporan}");
    await outTable.AddAsync(lap);
}

public enum TipeLaporan
{
    Jalanan = 1,
    Jembatan
}
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