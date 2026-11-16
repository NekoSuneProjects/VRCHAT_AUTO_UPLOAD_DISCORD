using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VRCX_Image_Discord_Uploader.Core;

/// <summary>A version like "2.1.0" (stable) or "2.1.0-beta-12" (beta build 12). Stable sorts after its betas.</summary>
public readonly partial record struct AppVersion(int Major, int Minor, int Patch, int Beta) : IComparable<AppVersion>
{
    public const int StableBeta = int.MaxValue;
    public bool IsBeta => Beta != StableBeta;

    // "2.1.0", "v2.1.0", "2.1.0-beta-12", "2.1.0-12" (beta release names), "2.1.0+sha"
    [GeneratedRegex(@"v?(\d+)\.(\d+)\.(\d+)(?:-(?:beta-?)?(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    public static AppVersion? Parse(string? text, bool forceBeta = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = VersionRegex().Match(text);
        if (!m.Success) return null;
        int beta = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : (forceBeta ? 0 : StableBeta);
        return new AppVersion(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), beta);
    }

    public int CompareTo(AppVersion o) =>
        (Major, Minor, Patch, Beta).CompareTo((o.Major, o.Minor, o.Patch, o.Beta));

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;

    public override string ToString() => IsBeta ? $"{Major}.{Minor}.{Patch} beta {Beta}" : $"{Major}.{Minor}.{Patch}";

    /// <summary>The running build. CI stamps InformationalVersion ("2.1.0-beta-12"); local builds fall back to Version.</summary>
    public static AppVersion Current
    {
        get
        {
            var asm = typeof(AppVersion).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return Parse(info) ?? Parse(asm.GetName().Version?.ToString(3)) ?? new AppVersion(0, 0, 0, StableBeta);
        }
    }
}

public record ReleaseInfo(AppVersion Version, string Name, string Tag, string PageUrl, string? ZipUrl, long ZipSize, string Notes, DateTime Published);

/// <summary>Looks for a newer release on GitHub (public API, no token needed).</summary>
public static class UpdateChecker
{
    public const string Repo = "NekoSuneProjects/VRCHAT_AUTO_UPLOAD_DISCORD";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
    const string Api = "https://api.github.com/repos/" + Repo + "/releases?per_page=30";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        // GitHub follows repo transfers with a redirect, which HttpClient follows automatically.
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VRCX-Image-Discord-Uploader", AppVersion.Current.ToString().Replace(' ', '-')));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    /// <summary>Newest release for the chosen channel, or null if none parse. Throws on network/API errors.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(bool includeBeta, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(Api, ct);
        bool limited = (int)resp.StatusCode == 429 ||
            ((int)resp.StatusCode == 403 && resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0");
        if (limited) throw new HttpRequestException("GitHub rate limit reached, try again later.");
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        return PickLatest(await resp.Content.ReadAsStringAsync(ct), includeBeta);
    }

    /// <summary>Picks the newest release from the GitHub /releases JSON.</summary>
    public static ReleaseInfo? PickLatest(string json, bool includeBeta)
    {
        using var doc = JsonDocument.Parse(json);
        ReleaseInfo? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;

            string tag = Str(r, "tag_name"), name = Str(r, "name");
            bool beta = (r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                        || tag.StartsWith("BETA", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("BETA", StringComparison.OrdinalIgnoreCase);
            if (beta && !includeBeta) continue;

            // Beta releases are named "... BETA v2.1.0-12" (tag "BETA-12"); stable ones are tagged "2.1.0".
            var v = beta ? AppVersion.Parse(name, forceBeta: true) : AppVersion.Parse(tag) ?? AppVersion.Parse(name);
            if (v == null) continue;
            if (beta && !v.Value.IsBeta) v = v.Value with { Beta = 0 };

            string? zip = null; long size = 0;
            if (r.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if (Str(a, "name").EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zip = Str(a, "browser_download_url");
                        size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                        break;
                    }

            DateTime.TryParse(Str(r, "published_at"), out var published);
            var info = new ReleaseInfo(v.Value, name.Length > 0 ? name : tag, tag, Str(r, "html_url"), zip, size, Str(r, "body"), published);
            if (best == null || info.Version > best.Version) best = info;
        }
        return best;
    }

    /// <summary>Newer release than the running one, or null if up to date.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(bool includeBeta, CancellationToken ct = default)
    {
        // A beta build always looks at betas too, otherwise it would never see the next beta.
        var current = AppVersion.Current;
        var latest = await GetLatestAsync(includeBeta || current.IsBeta, ct);
        return latest != null && latest.Version > current ? latest : null;
    }

    static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
