using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Net.Http;
using System.Web;
using System.Collections.Generic;
using System.Net;
using System.Drawing;
using System.Drawing.Imaging;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Azure.Storage.Blobs.Specialized;
using System.Numerics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace FunctionApp1
{
    public class BoundingBox
    {
        public double left { get; set; }
        public double top { get; set; }
        public double width { get; set; }
        public double height { get; set; }
    }
    public class Prediction
    {
        public double probability { get; set; }
        public string tagId { get; set; }
        public string tagName { get; set; }
        public BoundingBox boundingBox { get; set; }
    }
    public class Root
    {
        public string id { get; set; }
        public string project { get; set; }
        public string iteration { get; set; }
        public DateTime created { get; set; }
        public List<Prediction> predictions { get; set; }
    }


    public static class Function1
    {
        public static void StoreImage(string filePath, string fileName, string remoteFileURL, IConfiguration config)
        {
            string localFilePath = Path.Combine(filePath, fileName);
            string remoteFileUrl = remoteFileURL;
            WebClient webClient = new WebClient();
            webClient.DownloadFile(remoteFileUrl, localFilePath);
            webClient.Dispose();
            uploadpreprocessImageToBlob(filePath, fileName, config);
        }
        static async void uploadpreprocessImageToBlob(string filePath, string fileName, IConfiguration config)
        {
            string localFilePath = Path.Combine(filePath, fileName);

            BlobServiceClient blobServiceClient = new BlobServiceClient(config.GetConnectionString("storageConnectionString"));
            //set container name 
            string containerName = "preprocessimage";
            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            BlobClient blobClient = containerClient.GetBlobClient(fileName);
            // Open the file and upload its data
            using FileStream uploadFileStream = File.OpenRead(localFilePath);
            await blobClient.UploadAsync(uploadFileStream, true);
            uploadFileStream.Close();
        }

        static async void uploadPostProcessImage_getSASURI(string filePath, string fileName, string preProcessfileURL, int probability_threshold, string submit_by, ILogger log, IConfiguration config)
        {

            string localFilePath = Path.Combine(filePath, fileName);

            BlobServiceClient blobServiceClient = new BlobServiceClient(config.GetConnectionString("storageConnectionString"));
            //set container name 
            string containerName = "postprocessimage";
            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            BlobClient blobClient = containerClient.GetBlobClient(fileName);
            // Open the file and upload its data
            using FileStream uploadFileStream = File.OpenRead(localFilePath);
            await blobClient.UploadAsync(uploadFileStream, true);
            uploadFileStream.Close();
            log.LogInformation("Uploaded processed image...starting generating SAS URI ");
            if (blobClient.CanGenerateSasUri)
            {
                // SAS token that's valid for one year.
                BlobSasBuilder sasBuilder = new BlobSasBuilder()
                {
                    BlobContainerName = blobClient.GetParentBlobContainerClient().Name,
                    BlobName = blobClient.Name,
                    Resource = "b" //"b" if the shared resource is a blob. 
                };
                sasBuilder.ExpiresOn = DateTimeOffset.UtcNow.AddYears(1);
                sasBuilder.SetPermissions(BlobSasPermissions.Read | BlobSasPermissions.Write);
                Uri sasUri = blobClient.GenerateSasUri(sasBuilder);
                log.LogInformation(" Generated SAS URI...SQL qeury started ");
                //STORE DATA in SQL from HERE 
                try //adding try block, somehow below code doesn't work once deployed to AFN
                {
                    string insertQuery = "insert [dbo].[objectDetection] (PreProcess_ImageURL,PostProcess_ImageURL,Probability_Threshold, Submitted_By) values (" + "'" + preProcessfileURL.ToString() + "'" + ", '" + sasUri.ToString() + "', " + probability_threshold + ", '" + submit_by + "')";
                    SqlConnection connection = new SqlConnection(config.GetConnectionString("sqlConnectionString"));
                    connection.Open();
                    SqlCommand cmd = new SqlCommand(insertQuery, connection);
                    await cmd.ExecuteNonQueryAsync();
                    connection.Close();
                    
                }
                catch (Exception e)
                {
                    log.LogInformation(e.ToString());
                }

            }
        }
        public static async Task MakeRequest(string remoteFileURL, int probability_threshold, string submit_by, ILogger log, IConfiguration config)
        {
            
            var client = new HttpClient();
            var queryString = HttpUtility.ParseQueryString(string.Empty);
            
            client.DefaultRequestHeaders.Add("Prediction-key", config.GetConnectionString("Prediction-key").ToString());
            
            // Request parameters
            queryString["numTagsPerBoundingBox"] = config.GetConnectionString("RequestParameternumTagsPerBoundingBox").ToString();
            queryString["application"] = config.GetConnectionString("RequestParameterapplication").ToString();
            var customVisionURI = config.GetConnectionString("customVisionURI").ToString() + queryString;
            
            HttpResponseMessage response;
            // Request body
            string urlEncode = "{" + "\"url\"" + ":" + "\"" + remoteFileURL + "\"" + "}";

            byte[] byteData = Encoding.UTF8.GetBytes(urlEncode);
            var rand = new Random();

            //Setting Filename = UTC Day + Month + Year + Hour + Minute + random_integer 
            string localfilename = DateTime.UtcNow.Day.ToString() + DateTime.UtcNow.Month.ToString() + DateTime.UtcNow.Year.ToString() + DateTime.UtcNow.Hour.ToString() + DateTime.UtcNow.Minute.ToString() + rand.Next().ToString();

            string tempDir = Path.Combine(Path.GetTempPath(), "MyTempData");
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }
            string localTempFile = Path.Combine(tempDir, localfilename + ".jpg");

            StoreImage(tempDir, localfilename + ".jpg", remoteFileURL, config);

            log.LogInformation("Store Image.. ");
            using (var content = new ByteArrayContent(byteData))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                log.LogInformation("Calling custom vision API...");
                response = await client.PostAsync(customVisionURI, content);

                if ((int)response.StatusCode == 200)
                {
                    Console.WriteLine(await response.Content.ReadAsStringAsync());
                    Root myDeserializedClass = JsonConvert.DeserializeObject<Root>(await response.Content.ReadAsStringAsync());
                    //string fileName = tempFile;
                    using (Image image = Image.FromFile(localTempFile))
                    {
                        for (int i = 0; i < myDeserializedClass.predictions.Count; i++)
                        {
                            if (myDeserializedClass.predictions[i].probability * 100 >= probability_threshold) //Checking probability threshold 
                            {
                                using (Graphics graphic = Graphics.FromImage(image))
                                {
                                    //boundingBox values are in percent of the image original size, so you can draw the rectangle by multiplying the values by the image width (for left and width values) or by the image height (for top and height values)
                                    float left = (float)myDeserializedClass.predictions[i].boundingBox.left * image.Width;
                                    float top = (float)myDeserializedClass.predictions[i].boundingBox.top * image.Height;
                                    float width = (float)myDeserializedClass.predictions[i].boundingBox.width * image.Width;
                                    float height = (float)myDeserializedClass.predictions[i].boundingBox.height * image.Height;
                                    Pen blackPen = new Pen(Color.Red, 15);
                                    graphic.DrawRectangle(blackPen, left, top, width, height);
                                    Font font1 = new Font("Times New Roman", 18, FontStyle.Bold, GraphicsUnit.Pixel);
                                    PointF pointF1 = new PointF(left, top - 10);
                                    graphic.DrawString(myDeserializedClass.predictions[i].tagName + " -- " + myDeserializedClass.predictions[i].probability * 100, font1, Brushes.Black, pointF1);
                                }
                            }
                        }
                        string postTempFile = Path.Combine(tempDir, localfilename + "_Result.jpg");
                        image.Save(postTempFile, ImageFormat.Jpeg);
                        uploadPostProcessImage_getSASURI(tempDir, localfilename + "_Result.jpg", remoteFileURL, probability_threshold, submit_by, log, config);


                    }
                }
            }

        }

        [FunctionName("setImageClassification")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log, ExecutionContext context)
        {
            log.LogInformation("Request received, let's the fun begin :)");
            
            IConfiguration config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();
            
            string imageURI = req.Headers["Image_URL"];
            string probability_threshold = req.Headers["probability_threshold"].ToString();
            string submit_by = req.Headers["user"].ToString();
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            string responseMessage = "";
            try
            {
                
                await MakeRequest(imageURI, Int32.Parse(probability_threshold), submit_by, log, config);
                responseMessage = "Phewww!!! NZ--EastUS--NZ.. Ran faster than ever...All done. :) ";
            }
            catch (Exception ex)
            {
                responseMessage = "Grrrrr!!! I hate it..Encountered this exception :- " + ex.ToString();
            }
            return new OkObjectResult(responseMessage);
        }
    }
}
