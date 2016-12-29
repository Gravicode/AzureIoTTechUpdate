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
using Windows.Media.Capture;
using Windows.Storage;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.WindowsAzure.Storage.Blob;
using Windows.Storage.Pickers;
using Windows.UI.Xaml.Media.Imaging;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace CamApp
{

    public class ImageCaption : TableEntity
    {

        public ImageCaption()
        {
            this.PartitionKey = "BBB";
            this.RowKey = Guid.NewGuid().ToString();
        }
        public string ImageFile { get; set; }
        public int DetectedFace { set; get; }
        public string Description { set; get; }
        public bool IsAdult { set; get; }
        public bool IsRacy { set; get; }
        public string Tags { set; get; }
    }
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        readonly string ConnStr = "DefaultEndpointsProtocol=https;AccountName=funcstorage001;AccountKey=XTZ5CZRp6FcUSU6P3XtCaaWZ+AHLR5OUMqq8AaFbVtZWqxFcWq+1cW0gc5ZXBP/hho9lk7ThpmU+hCnZLT9qpw==";

        public MainPage()
        {
            this.InitializeComponent();
            BtnCam.Click += BtnCam_Click;
            ImageList.Click += ImageList_Click;
            BtnPick.Click += BtnPick_Click;
            LoadData();
        }

        private async void BtnPick_Click(object sender, RoutedEventArgs e)
        {
            FileOpenPicker openPicker = new FileOpenPicker();
            openPicker.ViewMode = PickerViewMode.Thumbnail;
            openPicker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            openPicker.FileTypeFilter.Add(".jpg");
            openPicker.FileTypeFilter.Add(".png");
            StorageFile file = await openPicker.PickSingleFileAsync();
            if (file != null)
            {
                var FileAsStream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
                
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(ConnStr);

                // Create the blob client.
                CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

                // Retrieve reference to a previously created container.
                CloudBlobContainer container = blobClient.GetContainerReference("photos");

                string photoname = "photo-" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss") + ".jpg";
                // Retrieve reference to a blob named "myblob".
                CloudBlockBlob blockBlob = container.GetBlockBlobReference(photoname);
               
                // Create or overwrite the "myblob" blob with contents from a local file.
                using (Stream stream = FileAsStream.AsStreamForRead())
                {
                    await blockBlob.UploadFromStreamAsync(stream);
                }
            }

            
        }

        private void ImageList_Click(object sender, RoutedEventArgs e)
        {
            LoadData();
        }

        async void LoadData()
        {
            // Retrieve the storage account from the connection string.
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(
                ConnStr);

            // Create the table client.
            CloudTableClient tableClient = storageAccount.CreateCloudTableClient();

            // Create the CloudTable object that represents the "people" table.
            CloudTable table = tableClient.GetTableReference("photocaption");

            // Construct the query operation for all customer entities where PartitionKey="Smith".
            TableQuery<ImageCaption> query = new TableQuery<ImageCaption>().Take(10);

            var datas = new List<ImageCaption>();
            string Prefix = "https://funcstorage001.blob.core.windows.net/photos/";
            // Print the fields for each customer.
            TableContinuationToken token = null;

            do
            {
                var seg = await table.ExecuteQuerySegmentedAsync(query, token);
                token = seg.ContinuationToken;
                foreach (var entity in seg.Results)
                {
                    entity.ImageFile = $"{Prefix}{entity.ImageFile}";
                    datas.Add(entity);
                }

            }
            while (token != null);
            ListGambar.ItemsSource = datas;
        }

        private async void BtnCam_Click(object sender, RoutedEventArgs e)
        {
            CameraCaptureUI captureUI = new CameraCaptureUI();
            captureUI.PhotoSettings.Format = CameraCaptureUIPhotoFormat.Jpeg;
            captureUI.PhotoSettings.CroppedSizeInPixels = new Size(200, 200);

            StorageFile photo = await captureUI.CaptureFileAsync(CameraCaptureUIMode.Photo);

            if (photo == null)
            {
                // User cancelled photo capture
                return;
            }
            
            // Retrieve storage account from connection string.
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(ConnStr);

            // Create the blob client.
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

            // Retrieve reference to a previously created container.
            CloudBlobContainer container = blobClient.GetContainerReference("photos");

            string photoname = "photo-" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss") + ".jpg";
            // Retrieve reference to a blob named "myblob".
            CloudBlockBlob blockBlob = container.GetBlockBlobReference(photoname);
            var randomAccessStream = await photo.OpenReadAsync();

            // Create or overwrite the "myblob" blob with contents from a local file.
            using (Stream stream = randomAccessStream.AsStreamForRead())
            {
                await blockBlob.UploadFromStreamAsync(stream);
            }
        }
    }
}
