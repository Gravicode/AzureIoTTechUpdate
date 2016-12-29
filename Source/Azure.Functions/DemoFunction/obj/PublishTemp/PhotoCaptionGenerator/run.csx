// Setup
// 1) Go to https://www.microsoft.com/cognitive-services/en-us/computer-vision-api 
//    Sign up for computer vision api
// 2) Go to Function app settings -> App Service settings -> Settings -> Application settings
//    create a new app setting Vision_API_Subscription_Key and use Computer vision key as value
#r "Microsoft.WindowsAzure.Storage"
#r "Newtonsoft.Json"
#r "System.Runtime"
#r "System.Threading.Tasks"
#r "System.IO"

using System.Net;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using Microsoft.WindowsAzure.Storage.Table;
using System.IO;
using Microsoft.ProjectOxford.Vision;
using Microsoft.ProjectOxford.Vision.Contract;
using System.Runtime;

public static async Task Run(Stream image, string name, IAsyncCollector<ImageCaption> outTable, TraceWriter log)
{
    var captionData = await CallVisionAPI(name, image, log);
    await outTable.AddAsync(captionData);
}
static async Task<ImageCaption> CallVisionAPI(string ImageName, Stream stream, TraceWriter log)
{
    var NewCaption = new ImageCaption();
    NewCaption.ImageFile = ImageName;
    log.Info("Calling VisionServiceClient.AnalyzeImageAsync()...");
    var VisionKey = Environment.GetEnvironmentVariable("COMPUTERVISION_KEY");
    VisionServiceClient VisionServiceClient = new VisionServiceClient(VisionKey);
    VisualFeature[] visualFeatures = new VisualFeature[] { VisualFeature.Adult, VisualFeature.Categories, VisualFeature.Color, VisualFeature.Description, VisualFeature.Faces, VisualFeature.ImageType, VisualFeature.Tags };
    AnalysisResult result = await VisionServiceClient.AnalyzeImageAsync(stream, visualFeatures);
    string Speak = string.Empty;
    if (result == null)
    {
        log.Info("null");
        return null;
    }

    if (result.Metadata != null)
    {
        log.Info("Image Format : " + result.Metadata.Format);
        log.Info("Image Dimensions : " + result.Metadata.Width + " x " + result.Metadata.Height);
    }

    if (result.ImageType != null)
    {
        string clipArtType;
        switch (result.ImageType.ClipArtType)
        {
            case 0:
                clipArtType = "0 Non-clipart";
                break;
            case 1:
                clipArtType = "1 ambiguous";
                break;
            case 2:
                clipArtType = "2 normal-clipart";
                break;
            case 3:
                clipArtType = "3 good-clipart";
                break;
            default:
                clipArtType = "Unknown";
                break;
        }
        log.Info("Clip Art Type : " + clipArtType);

        string lineDrawingType;
        switch (result.ImageType.LineDrawingType)
        {
            case 0:
                lineDrawingType = "0 Non-LineDrawing";
                break;
            case 1:
                lineDrawingType = "1 LineDrawing";
                break;
            default:
                lineDrawingType = "Unknown";
                break;
        }
        log.Info("Line Drawing Type : " + lineDrawingType);
    }


    if (result.Adult != null)
    {
        NewCaption.IsAdult = result.Adult.IsAdultContent;
        log.Info("Is Adult Content : " + result.Adult.IsAdultContent);
        log.Info("Adult Score : " + result.Adult.AdultScore);
        NewCaption.IsRacy = result.Adult.IsRacyContent;
        log.Info("Is Racy Content : " + result.Adult.IsRacyContent);
        log.Info("Racy Score : " + result.Adult.RacyScore);
    }

    if (result.Categories != null && result.Categories.Length > 0)
    {
        log.Info("Categories : ");
        foreach (var category in result.Categories)
        {
            log.Info("Name : " + category.Name + "; Score : " + category.Score);
        }
    }

    if (result.Faces != null && result.Faces.Length > 0)
    {
        log.Info("Faces : ");
        foreach (var face in result.Faces)
        {
            log.Info("Age : " + face.Age + "; Gender : " + face.Gender);
        }
        NewCaption.DetectedFace = result.Faces.Length;
    }

    if (result.Color != null)
    {
        log.Info("AccentColor : " + result.Color.AccentColor);
        log.Info("Dominant Color Background : " + result.Color.DominantColorBackground);
        log.Info("Dominant Color Foreground : " + result.Color.DominantColorForeground);

        if (result.Color.DominantColors != null && result.Color.DominantColors.Length > 0)
        {
            string colors = "Dominant Colors : ";
            foreach (var color in result.Color.DominantColors)
            {
                colors += color + " ";
            }
            log.Info(colors);
        }
    }

    if (result.Description != null)
    {
        log.Info("Description : ");
        foreach (var caption in result.Description.Captions)
        {
            log.Info("   Caption : " + caption.Text + "; Confidence : " + caption.Confidence);
            Speak += caption.Text;
        }
        string tags = "   Tags : ";
        foreach (var tag in result.Description.Tags)
        {
            tags += tag + ", ";
        }
        log.Info(tags);

    }

    if (result.Tags != null)
    {
        log.Info("Tags : ");
        NewCaption.Tags = "";
        foreach (var tag in result.Tags)
        {
            NewCaption.Tags += $"{tag.Name};";
            log.Info("   Name : " + tag.Name + "; Confidence : " + tag.Confidence + "; Hint : " + tag.Hint);
        }
    }
    NewCaption.Description = Speak;
    return NewCaption;
}


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