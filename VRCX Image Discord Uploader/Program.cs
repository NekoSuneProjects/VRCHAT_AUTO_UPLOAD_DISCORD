using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using DotNetEnv;
using MetadataExtractor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static System.Net.Mime.MediaTypeNames;
using Directory = MetadataExtractor.Directory;

class Program
{
    static string webhookUrl;
    static string webhookAuthKey;
    static string directoryToMonitor;

    static async Task Main(string[] args)
    {
        // Load environment variables from .env file
        Env.Load(".env");

        // Get webhook URL from environment variable
        webhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
        webhookAuthKey = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_AuthKey");

        // Get the current user's username
        string username = Environment.UserName;

        // Specify the directory to monitor
        directoryToMonitor = Environment.GetEnvironmentVariable("VRCHAT_IMAGE_PATH");

        // Process existing files
        await ProcessExistingFiles(directoryToMonitor);

        // Start checking for new files in the directory
        await CheckForNewFiles(directoryToMonitor);
    }

    static async Task WaitForFile(string filePath, int retries = 5, int delay = 500)
    {
    for (int i = 0; i < retries; i++)
    {
        if (File.Exists(filePath))
        {
            try
            {
                using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    return; // File is ready
                }
            }
            catch (IOException)
            {
                // File is still in use, wait and retry
            }
        }
        await Task.Delay(delay);
    }
    throw new FileNotFoundException($"File not accessible: {filePath}");
    }


    static string ExtractJsonDescription(string filePath)
    {
        var description = "";
        // Read the metadata from the image file
        IEnumerable<Directory> directories = ImageMetadataReader.ReadMetadata(filePath);

        foreach (var directory in directories)
            foreach (var tag in directory.Tags)
                if (tag.Name == "Textual Data")
                description = tag.Description;

        // Get the description tag (which might contain JSON)
        if (string.IsNullOrEmpty(description))
        {
            throw new Exception("The selected image has no description metadata.");
        }

        const string prefix = "Description: ";
        if (description.StartsWith(prefix))
        {
            return description.Substring(prefix.Length).Trim();
        }

        Console.WriteLine(description);

        return description;
    }

    static (string, List<string>) ExtractImageMetadata(string description)
    {
            

            // Deserialize metadata JSON from description
            var metadata = JsonConvert.DeserializeObject<JObject>(description);

            // Extract required fields from metadata
            string worldName = "["+ metadata["world"]["name"].ToString() + "](<https://vrchat.com/home/world/"+ metadata["world"]["id"].ToString() + ">)";
            //string worldId = metadata["world"]["id"].ToString();

            var playerNames = metadata["players"]
                .ToObject<List<JObject>>()
                .Select(player => "["+player["displayName"].ToString()+ "](<https://vrchat.com/home/user/" + player["id"].ToString() + ">)")
                .ToList();

            return (worldName, playerNames);
    }



    static async Task<string> CreatePayload(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        long timestamp = new DateTimeOffset(fileInfo.CreationTime).ToUnixTimeSeconds();
        var Jsondata = ExtractJsonDescription(filePath);;

        (string worldName, List<string> playerNames) = ExtractImageMetadata(Jsondata);

        var payload = new
        {
            content = $"Photo taken at **{worldName}** with **{string.Join(", ", playerNames)}** at <t:{timestamp}:f>"
        };

        return $"Photo taken at **{worldName}** with **{string.Join(", ", playerNames)}** at <t:{timestamp}:f>"; //JsonConvert.SerializeObject(payload);
    }

    static async Task UploadImageToDiscord(string filePath)
    {
        try
        {
            // Check if webhook URL is available
            if (string.IsNullOrEmpty(webhookUrl))
            {
                throw new Exception("Discord webhook URL is not set.");
            }

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + webhookAuthKey);

                var form = new MultipartFormDataContent();
                form.Headers.ContentType.MediaType = "multipart/form-data";

                // Read the image file and add it to the form content
                byte[] imageData = File.ReadAllBytes(filePath);
                var imageContent = new ByteArrayContent(imageData);
                imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                form.Add(new StringContent(await CreatePayload(filePath)), "content");
                form.Add(new StringContent("false"), "isEmbed");
                form.Add(imageContent, "file", Path.GetFileName(filePath));

                // Send the request
                var response = await httpClient.PostAsync(webhookUrl, form);

                // Check the response
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine("Image uploaded successfully");
                }
                else
                {
                    Console.WriteLine("Failed to upload image: " + response.ReasonPhrase);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("Error: " + e.Message);
        }
    }


    static async Task CheckForNewFiles(string directory)
    {
        var currentFiles = new HashSet<string>();

        try
        {
            FileSystemWatcher watcher = new FileSystemWatcher();
            watcher.Path = directory;

            // Set to monitor both current directory and subdirectories
            watcher.IncludeSubdirectories = true;

            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime;
            watcher.Filter = "*.png";

            watcher.Created += async (sender, e) =>
            {
                // Replace backslashes with forward slashes in file path
                var fullPath = e.FullPath.Replace("\\", "/");

                Console.WriteLine($"New file created: {fullPath}");

                // Add a delay before processing the file
                await Task.Delay(1000); // Adjust the delay time as needed (e.g., 1000 ms = 1 second)

                if (!currentFiles.Contains(fullPath))
                {
                    await WaitForFile(fullPath);
                    currentFiles.Add(fullPath);
                    await UploadImageToDiscord(fullPath);
                }
            };

            watcher.EnableRaisingEvents = true;

            // Keep the program running
            await Task.Delay(-1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in CheckForNewFiles: {ex.Message}");
        }
    }



    static async Task ProcessExistingFiles(string directory)
    {
        var files = System.IO.Directory.GetFiles(directory, "*.png");
        foreach (var file in files)
        {
            await UploadImageToDiscord(file);
        }
    }
}
