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
    static DateTime appStartTime;

    // Track processed screenshots (by base name)
    static HashSet<string> processedBaseNames = new HashSet<string>();

    static async Task Main(string[] args)
    {
        Env.Load(".env");

        webhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
        webhookAuthKey = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_AuthKey");
        directoryToMonitor = Environment.GetEnvironmentVariable("VRCHAT_IMAGE_PATH");

        appStartTime = DateTime.Now;

        await CheckForNewFiles(directoryToMonitor);
    }

    // 🔒 Wait until file is fully written & unlocked
    static async Task WaitForFileReady(string path, int retries = 15, int delay = 500)
    {
        for (int i = 0; i < retries; i++)
        {
            if (File.Exists(path))
            {
                try
                {
                    using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                        return;
                }
                catch { }
            }

            await Task.Delay(delay);
        }

        throw new Exception("File never became ready.");
    }

    // 🔍 Wait for rename OR final version
    static async Task<string> WaitForFinalFile(string originalPath)
    {
        string dir = Path.GetDirectoryName(originalPath);
        string baseName = Path.GetFileNameWithoutExtension(originalPath);

        for (int i = 0; i < 10; i++)
        {
            var files = System.IO.Directory.GetFiles(dir, baseName + "*");

            // Prefer renamed version
            var renamed = files.FirstOrDefault(f => f.Contains("_wrld_"));
            if (renamed != null)
                return renamed;

            if (File.Exists(originalPath))
                return originalPath;

            await Task.Delay(500);
        }

        return null;
    }

    // 📦 Wait until metadata exists
    static async Task<string> WaitForMetadata(string filePath)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                var directories = ImageMetadataReader.ReadMetadata(filePath);

                foreach (var directory in directories)
                {
                    foreach (var tag in directory.Tags)
                    {
                        if (tag.Name == "Textual Data")
                        {
                            var desc = tag.Description;

                            if (!string.IsNullOrEmpty(desc))
                            {
                                const string prefix = "Description: ";
                                if (desc.StartsWith(prefix))
                                    return desc.Substring(prefix.Length).Trim();

                                return desc;
                            }
                        }
                    }
                }
            }
            catch { }

            await Task.Delay(500);
        }

        return null;
    }

    // 🧠 Normalize filename (remove _wrld_ part)
    static string GetBaseName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);

        int index = name.IndexOf("_wrld_");
        if (index != -1)
            name = name.Substring(0, index);

        return name;
    }

    static (string, List<string>) ExtractImageMetadata(string description)
    {
        var metadata = JsonConvert.DeserializeObject<JObject>(description);

        string world =
            "[" + metadata["world"]["name"] + "](<https://vrchat.com/home/world/" + metadata["world"]["id"] + ">)";

        var players = metadata["players"]
            .ToObject<List<JObject>>()
            .Select(p =>
                "[" + p["displayName"] + "](<https://vrchat.com/home/user/" + p["id"] + ">)")
            .ToList();

        return (world, players);
    }

    static async Task UploadImageToDiscord(string filePath, string jsonData)
    {
        try
        {
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + webhookAuthKey);

                var (world, players) = ExtractImageMetadata(jsonData);

                var payload = JsonConvert.SerializeObject(new
                {
                    world = world,
                    players = players
                });

                var form = new MultipartFormDataContent();

                byte[] imageData = File.ReadAllBytes(filePath);
                var imageContent = new ByteArrayContent(imageData);
                imageContent.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                form.Add(new StringContent(payload), "vrcjson");
                form.Add(new StringContent("false"), "isEmbed");
                form.Add(imageContent, "file", Path.GetFileName(filePath));

                var response = await httpClient.PostAsync(webhookUrl, form);

                Console.WriteLine(response.IsSuccessStatusCode
                    ? $"✅ Uploaded: {filePath}"
                    : $"❌ Upload failed: {response.ReasonPhrase}");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("Error: " + e.Message);
        }
    }

    // 🧠 MASTER HANDLER
    static async Task HandleFile(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);

            // 🚫 Ignore old files
            if (fileInfo.CreationTime < appStartTime)
                return;

            string baseName = GetBaseName(path);

            // 🚫 Already processed
            if (processedBaseNames.Contains(baseName))
                return;

            Console.WriteLine($"Detected: {path}");

            string finalPath = await WaitForFinalFile(path);
            if (finalPath == null)
                return;

            baseName = GetBaseName(finalPath);

            if (processedBaseNames.Contains(baseName))
                return;

            await WaitForFileReady(finalPath);

            var metadata = await WaitForMetadata(finalPath);

            if (metadata == null)
            {
                Console.WriteLine($"⚠️ Skipped (no metadata): {finalPath}");
                return;
            }

            // ✅ mark BEFORE upload (prevents duplicates)
            processedBaseNames.Add(baseName);

            await UploadImageToDiscord(finalPath, metadata);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Skipped: {ex.Message}");
        }
    }

    static async Task CheckForNewFiles(string directory)
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
            await HandleFile(e.FullPath.Replace("\\", "/"));
        };

        watcher.Renamed += async (s, e) =>
        {
            await HandleFile(e.FullPath.Replace("\\", "/"));
        };

        watcher.EnableRaisingEvents = true;

        Console.WriteLine("👀 Watching for new VRChat screenshots...");

        await Task.Delay(-1);
    }
}
