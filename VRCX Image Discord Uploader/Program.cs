using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DotNetEnv;
using MetadataExtractor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;
using Directory = MetadataExtractor.Directory;

class Program
{
    static string webhookUrl;
    static string webhookAuthKey;
    static string directoryToMonitor;

    static HashSet<string> processedFiles = new HashSet<string>();

    static async Task Main(string[] args)
    {
        Env.Load(".env");

        webhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
        webhookAuthKey = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_AuthKey");
        directoryToMonitor = Environment.GetEnvironmentVariable("VRCHAT_IMAGE_PATH");

        await ProcessExistingFiles(directoryToMonitor);
        await CheckForNewFiles(directoryToMonitor);
    }

    static async Task WaitForFile(string filePath, int retries = 10, int delay = 500)
    {
        for (int i = 0; i < retries; i++)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        return;
                    }
                }
                catch (IOException) { }
            }
            await Task.Delay(delay);
        }
        throw new FileNotFoundException($"File not accessible: {filePath}");
    }

    static string FindFinalFile(string originalPath)
    {
        if (!File.Exists(originalPath))
        {
            string dir = Path.GetDirectoryName(originalPath);
            string baseName = Path.GetFileNameWithoutExtension(originalPath);

            var matches = System.IO.Directory.GetFiles(dir, baseName + "*");

            // Prefer renamed version with _wrld_
            var renamed = matches.FirstOrDefault(f => f.Contains("_wrld_"));
            return renamed ?? matches.FirstOrDefault();
        }

        return originalPath;
    }

    static string ExtractJsonDescription(string filePath)
    {
        var description = "";

        var directories = ImageMetadataReader.ReadMetadata(filePath);

        foreach (var directory in directories)
            foreach (var tag in directory.Tags)
                if (tag.Name == "Textual Data")
                    description = tag.Description;

        if (string.IsNullOrEmpty(description))
            throw new Exception("No metadata found.");

        const string prefix = "Description: ";
        if (description.StartsWith(prefix))
            return description.Substring(prefix.Length).Trim();

        return description;
    }

    static (string, List<string>) ExtractImageMetadata(string description)
    {
        var metadata = JsonConvert.DeserializeObject<JObject>(description);

        string worldName =
            "[" + metadata["world"]["name"] + "](<https://vrchat.com/home/world/" + metadata["world"]["id"] + ">)";

        var players = metadata["players"]
            .ToObject<List<JObject>>()
            .Select(p =>
                "[" + p["displayName"] + "](<https://vrchat.com/home/user/" + p["id"] + ">)")
            .ToList();

        return (worldName, players);
    }

    static async Task<string> CreatePayload(string filePath)
    {
        var jsonData = ExtractJsonDescription(filePath);
        var (world, players) = ExtractImageMetadata(jsonData);

        var payload = new
        {
            world = world,
            players = players
        };

        return JsonConvert.SerializeObject(payload);
    }

    static async Task UploadImageToDiscord(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(webhookUrl))
                throw new Exception("Webhook not set.");

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + webhookAuthKey);

                var form = new MultipartFormDataContent();

                byte[] imageData = File.ReadAllBytes(filePath);
                var imageContent = new ByteArrayContent(imageData);
                imageContent.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                form.Add(new StringContent(await CreatePayload(filePath)), "vrcjson");
                form.Add(new StringContent("false"), "isEmbed");
                form.Add(imageContent, "file", Path.GetFileName(filePath));

                var response = await httpClient.PostAsync(webhookUrl, form);

                Console.WriteLine(response.IsSuccessStatusCode
                    ? $"Uploaded: {filePath}"
                    : $"Upload failed: {response.ReasonPhrase}");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("Error: " + e.Message);
        }
    }

    static async Task HandleFile(string path)
    {
        await Task.Delay(1500); // wait for rename/write

        string finalPath = FindFinalFile(path);

        if (finalPath == null)
            return;

        if (processedFiles.Contains(finalPath))
            return;

        try
        {
            await WaitForFile(finalPath);
            processedFiles.Add(finalPath);
            await UploadImageToDiscord(finalPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Skipped: {ex.Message}");
        }
    }

    static async Task CheckForNewFiles(string directory)
    {
        try
        {
            var watcher = new FileSystemWatcher
            {
                Path = directory,
                IncludeSubdirectories = true,
                Filter = "*.png",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime
            };

            watcher.Created += async (s, e) =>
            {
                var path = e.FullPath.Replace("\\", "/");
                Console.WriteLine($"Created: {path}");
                await HandleFile(path);
            };

            watcher.Renamed += async (s, e) =>
            {
                var path = e.FullPath.Replace("\\", "/");
                Console.WriteLine($"Renamed: {path}");
                await HandleFile(path);
            };

            watcher.EnableRaisingEvents = true;

            await Task.Delay(-1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Watcher error: {ex.Message}");
        }
    }

    static async Task ProcessExistingFiles(string directory)
    {
        var files = System.IO.Directory.GetFiles(directory, "*.png", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            await HandleFile(file);
        }
    }
}
