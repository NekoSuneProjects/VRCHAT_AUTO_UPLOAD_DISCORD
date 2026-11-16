using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VRCX_Image_Discord_Uploader.Core;

public record UploadResult(bool Success, string Message);

public static class DiscordUploader
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public static string BuildPayload(ScreenshotInfo info, bool includePlayers)
    {
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

        // The bot always expects both fields – sending "{}" is what caused "Bad Request".
        return JsonSerializer.Serialize(new { world, players }, Json.Compact);
    }

    static string Escape(string s) => s.Replace("[", "(").Replace("]", ")");

    public static async Task<UploadResult> UploadAsync(AppSettings s, byte[] image, string fileName, string payload, CancellationToken ct = default)
    {
        var url = s.ResolveUploadUrl();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            return new(false, "Upload URL is not set (Settings → Discord).");
        if (string.IsNullOrWhiteSpace(s.AuthKey))
            return new(false, "Auth key is not set (Settings → Discord).");

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.AuthKey.Trim());

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
                if (!string.IsNullOrWhiteSpace(body)) msg += " – " + Trim(body);

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

    static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }
}
