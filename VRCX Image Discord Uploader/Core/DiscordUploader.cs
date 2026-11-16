using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VRCX_Image_Discord_Uploader.Core;

public record UploadResult(bool Success, string Message, bool Duplicate = false);

/// <summary>What Nekumi's /info says about a key: where it posts and the rules there.</summary>
public record DestinationInfo(string KeyType, string Name, string ServerName, string ChannelName, string Visibility, List<string> Rules);

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
                // Nekumi: this photo is already in that channel – that's fine, nothing to retry.
                if ((int)resp.StatusCode == 409 && IsDuplicate(body)) return new(true, "Already posted there", Duplicate: true);

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

    static bool IsDuplicate(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("duplicate", out var d) && d.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    /// <summary>".../integration/vrchat/v1/upload" -> ".../integration/vrchat/v1/{name}", or null for non-Nekumi URLs.</summary>
    static Uri? NekumiEndpoint(UploadDestination d, string name)
    {
        if (!Uri.TryCreate(d.ResolveUrl(), UriKind.Absolute, out var uri)) return null;
        int i = uri.AbsolutePath.IndexOf("/integration/vrchat/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        return new UriBuilder(uri) { Path = uri.AbsolutePath[..i] + "/integration/vrchat/v1/" + name, Query = "" }.Uri;
    }

    /// <summary>
    /// Destination name, server/channel, public/private and rules for this key. Null when the server doesn't
    /// support it (old bot / custom URL). Throws with a readable message for a bad key or network error.
    /// </summary>
    public static async Task<DestinationInfo?> GetInfoAsync(UploadDestination d, CancellationToken ct = default)
    {
        var url = NekumiEndpoint(d, "info");
        if (url == null || d.AuthKey.Trim().Length == 0) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", d.AuthKey.Trim());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.SendAsync(req, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if ((int)resp.StatusCode == 404) return null; // Nekumi without /info yet
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase} – {ErrorText(body)}");

        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        string S(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var rules = new List<string>();
        if (r.TryGetProperty("rules", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString())) rules.Add(x.GetString()!.Trim());
        return new DestinationInfo(
            S(r, "keyType"), S(r, "name"),
            r.TryGetProperty("guild", out var g) ? S(g, "name") : "",
            r.TryGetProperty("channel", out var c) ? S(c, "name") : "",
            S(r, "visibility"), rules);
    }

    /// <summary>Which of these SHA-256 hashes the server already has in this key's channel. Null if unsupported/unreachable.</summary>
    public static async Task<HashSet<string>?> CheckPostedAsync(UploadDestination d, IEnumerable<string> hashes, CancellationToken ct = default)
    {
        var url = NekumiEndpoint(d, "check");
        if (url == null || d.AuthKey.Trim().Length == 0) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { hashes = hashes.ToArray() }), System.Text.Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", d.AuthKey.Trim());
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("posted", out var p) && p.ValueKind == JsonValueKind.Array)
                foreach (var x in p.EnumerateArray()) if (x.GetString() is { } h) set.Add(h);
            return set;
        }
        catch { return null; }
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

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
