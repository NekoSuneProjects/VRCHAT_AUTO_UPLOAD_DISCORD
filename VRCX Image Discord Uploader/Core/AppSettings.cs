using System.Text.Json.Serialization;

namespace VRCX_Image_Discord_Uploader.Core;

public enum DestinationKind
{
    /// <summary>A shared community server (e.g. NEKOSUNECOMMUNITY). Personal key from /vrchatupload key.</summary>
    Community,
    /// <summary>The user's own server. Key from the Nekumi dashboard → Integrations → VRChat.</summary>
    OwnServer,
    /// <summary>Any other compatible upload URL (old bot, self-hosted).</summary>
    Custom,
}

/// <summary>
/// One place screenshots get posted to. With Nekumi every destination uses the same URL –
/// the key itself decides which server/channel it posts in.
/// </summary>
public class UploadDestination
{
    public const string NekumiUploadUrl = "https://nekumi.nekosunevr.co.uk/integration/vrchat/v1/upload";
    public const string NekumiDashboardUrl = "https://nekumi.nekosunevr.co.uk/dashboard";
    public const string CommunityKey = "NEKOSUNECOMMUNITY";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public DestinationKind Kind { get; set; } = DestinationKind.OwnServer;
    public string Url { get; set; } = NekumiUploadUrl;
    public bool Enabled { get; set; } = true;

    /// <summary>Plain key, memory only. Saved encrypted in <see cref="AuthKeyProtected"/>.</summary>
    [JsonIgnore] public string AuthKey { get; set; } = "";
    public string AuthKeyProtected { get; set; } = "";

    // ---- From the server (Nekumi /info), refreshed on start and every few hours ----
    public List<string> Rules { get; set; } = new();
    /// <summary>Hash of the rules the user accepted. If the server changes its rules, auto-upload pauses until accepted again.</summary>
    public string AcceptedRulesHash { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ChannelName { get; set; } = "";
    /// <summary>"public" / "private" / "" (unknown or not a Nekumi server).</summary>
    public string Visibility { get; set; } = "";

    [JsonIgnore] public string RulesHash => Rules.Count == 0 ? "" :
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", Rules))));
    [JsonIgnore] public bool NeedsRulesAcceptance => Rules.Count > 0 && AcceptedRulesHash != RulesHash;
    [JsonIgnore] public string InfoText
    {
        get
        {
            var where = ServerName.Length > 0 ? ServerName + (ChannelName.Length > 0 ? " › #" + ChannelName : "") : "";
            var vis = Visibility switch { "public" => "Public", "private" => "Private", _ => "" };
            var parts = new[] { where, vis, NeedsRulesAcceptance ? "⚠ new rules – review" : Rules.Count > 0 ? $"{Rules.Count} rule(s)" : "" }
                .Where(p => p.Length > 0);
            return string.Join("  ·  ", parts);
        }
    }

    // Only used by old-style URLs: https://host/v1/discord/webhooks/{serverId}/{channelId}/upload/text
    public string ServerId { get; set; } = "";
    public string ChannelId { get; set; } = "";

    public string ResolveUrl() => Url.Trim()
        .Replace("{serverId}", ServerId.Trim(), StringComparison.OrdinalIgnoreCase)
        .Replace("{channelId}", ChannelId.Trim(), StringComparison.OrdinalIgnoreCase);

    public static UploadDestination NekoSuneCommunity() => new()
    {
        Name = CommunityKey,
        Kind = DestinationKind.Community,
        Url = NekumiUploadUrl,
    };

    /// <summary>What a key looks like, so the UI can warn about a key pasted into the wrong slot.</summary>
    public static string DescribeKey(string key)
    {
        key = key.Trim();
        if (key.StartsWith("nkvrm_", StringComparison.Ordinal)) return "Community member key";
        if (key.StartsWith("nkvrc_", StringComparison.Ordinal)) return "Server integration key";
        return key.Length == 0 ? "No key" : "Custom key";
    }

    [JsonIgnore] public string KindText => Kind switch
    {
        DestinationKind.Community => "Community",
        DestinationKind.OwnServer => "My server",
        _ => "Custom",
    };

    [JsonIgnore] public string KeyHint
    {
        get
        {
            var k = AuthKey.Trim();
            if (k.Length == 0) return "No key set";
            return k.Length <= 14 ? new string('•', k.Length) : $"{k[..10]}…{k[^4..]}";
        }
    }

    public UploadDestination Clone()
    {
        var c = (UploadDestination)MemberwiseClone();
        c.Rules = Rules.ToList();
        return c;
    }
}

public class AppSettings
{
    /// <summary>Bump when the file layout changes and add a step in <see cref="SettingsStore"/>.Migrate.</summary>
    public const int CurrentSchema = 2;

    public int SchemaVersion { get; set; } = CurrentSchema;

    // ---- Where to post ----
    public List<UploadDestination> Destinations { get; set; } = new();

    // ---- Screenshots ----
    public string ScreenshotFolder { get; set; } = AppPaths.DefaultScreenshotDir;
    public bool IncludeSubfolders { get; set; } = true;
    /// <summary>How long to wait after the last file event before uploading (VRCX renames / writes metadata).</summary>
    public int SettleDelayMs { get; set; } = 2500;
    public bool UploadWithMetadata { get; set; } = true;
    public bool IncludePlayers { get; set; } = true;

    // ---- App behaviour ----
    public bool StartWatchingOnLaunch { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool ShowNotifications { get; set; } = true;
    public int HistoryLimit { get; set; } = 500;

    // ---- Updates ----
    public bool CheckForUpdates { get; set; } = true;
    public bool BetaUpdates { get; set; } = false;
    /// <summary>Version the user clicked "Skip" on; the banner stays hidden until something newer.</summary>
    public string SkippedVersion { get; set; } = "";
    /// <summary>Version that last ran; when it changes the app shows "What's new".</summary>
    public string LastRunVersion { get; set; } = "";

    [JsonIgnore] public IEnumerable<UploadDestination> ActiveDestinations =>
        Destinations.Where(d => d.Enabled && !d.NeedsRulesAcceptance && !string.IsNullOrWhiteSpace(d.Url) && !string.IsNullOrWhiteSpace(d.AuthKey));

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.Destinations = Destinations.Select(d => d.Clone()).ToList();
        return c;
    }
}
