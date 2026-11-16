using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VRCX_Image_Discord_Uploader.Core;

public record UploadResult(bool Success, string Message);

public static class DiscordUploader
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public static string BuildPayload(ScreenshotInfo info, bool includePlayers, DateTime? takenAt = null)
    {
        // Older photos (gallery / late uploads) get their real date, since the Discord post shows the upload time.
        string message = takenAt is { } t && DateTime.Now - t > TimeSpan.FromMinutes(15)
            ? $"📸 Taken {t:dddd d MMMM yyyy, HH:mm}"
            : "";

        string world;
        if (!string.IsNullOrEmpty(info.WorldId))
        {
            var name = string.IsNullOrEmpty(info.WorldName) ? "Unknown World" : info.WorldName;
            world = $"[{Escape(name)}](<https://vrchat.com/home/world/{info.WorldId}>)";
        }
        else
        {
            world = string.IsNullOrEmpty(info.WorldName) ? "Unknown World" : Escape(info.WorldName);
        }

        var players = includePlayers
            ? info.Players.Select(p => string.IsNullOrEmpty(p.Id)
                    ? Escape(p.DisplayName)
                    : $"[{Escape(p.DisplayName)}](<https://vrchat.com/home/user/{p.Id}>)").ToList()
            : new List<string>();

        // world + players: what the original bot expects (sending "{}" caused "Bad Request").
        // worldId + instanceId: extra fields Nekumi shows in the embed; older bots ignore them.
        return JsonSerializer.Serialize(new
        {
            world,
            worldId = info.WorldId,
            instanceId = info.InstanceId,
            players,
            message,
        }, Json.Compact);
    }

    static string Escape(string s) => s.Replace("[", "(").Replace("]", ")");

    public static async Task<UploadResult> UploadAsync(UploadDestination d, byte[] image, string fileName, string payload, CancellationToken ct = default)
    {
        var url = d.ResolveUrl();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            return new(false, "Upload URL is not set.");
        if (string.IsNullOrWhiteSpace(d.AuthKey))
            return new(false, "Key is not set.");

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", d.AuthKey.Trim());

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(payload), "vrcjson");
                form.Add(new StringContent("false"), "isEmbed");
                var img = new ByteArrayContent(image);
                img.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); // same as the original client
                form.Add(img, "file", fileName);
                req.Content = form;

                using var resp = await Http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode) return new(true, "Uploaded");

                var msg = $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
                var detail = ErrorText(body);
                if (!string.IsNullOrWhiteSpace(detail)) msg += " – " + detail;

                // client errors won't fix themselves by retrying (except rate limit)
                if ((int)resp.StatusCode is >= 400 and < 500 and not 429) return new(false, msg);
                if (attempt == 3) return new(false, msg);
                Log.Warn($"Upload attempt {attempt} failed ({msg}), retrying…");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt == 3) return new(false, "Network error: " + ex.Message);
                Log.Warn($"Upload attempt {attempt} failed ({ex.Message}), retrying…");
            }
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
        }
        return new(false, "Upload failed");
    }

    /// <summary>Nekumi answers {"ok":false,"error":"..."}; show just the message when it does.</summary>
    static string ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return Trim(e.GetString() ?? "");
        }
        catch { }
        return Trim(body);
    }

    /// <summary>Checks the server is reachable without posting anything (Nekumi /integration/vrchat/health).</summary>
    public static async Task<UploadResult> CheckAsync(UploadDestination d)
    {
        if (!Uri.TryCreate(d.ResolveUrl(), UriKind.Absolute, out var uri)) return new(false, "Upload URL is not valid.");
        var path = uri.AbsolutePath;
        int i = path.IndexOf("/integration/vrchat/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return new(true, "URL looks valid (not a Nekumi server, so it can't be checked without posting).");
        var health = new UriBuilder(uri) { Path = path[..i] + "/integration/vrchat/health", Query = "" }.Uri;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var resp = await Http.GetAsync(health, cts.Token);
            return resp.IsSuccessStatusCode
                ? new(true, "Nekumi is reachable.")
                : new(false, $"Server answered {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        }
        catch (Exception ex) { return new(false, "Can't reach server: " + ex.Message); }
    }

    static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }
}
