using System.Text.Json.Serialization;

namespace VRCX_Image_Discord_Uploader.Core;

public class AppSettings
{
    /// <summary>Bump when the file layout changes and add a step in <see cref="SettingsStore"/>.Migrate.</summary>
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;

    // ---- Discord / bot ----
    public string WebhookUrl { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ChannelId { get; set; } = "";

    /// <summary>Plain auth key, only in memory. Stored on disk encrypted (DPAPI, current Windows user).</summary>
    [JsonIgnore] public string AuthKey { get; set; } = "";
    public string AuthKeyProtected { get; set; } = "";

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

    public string ResolveUploadUrl()
    {
        // Allows URLs like https://host/upload/{serverId}/{channelId}
        return WebhookUrl.Trim()
            .Replace("{serverId}", ServerId.Trim(), StringComparison.OrdinalIgnoreCase)
            .Replace("{channelId}", ChannelId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.AuthKey = AuthKey;
        return c;
    }
}
