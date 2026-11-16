using System.IO;

namespace VRCX_Image_Discord_Uploader.Core;

/// <summary>
/// Every piece of user data lives in %APPDATA%\NekoSuneVR\VRCX Image Discord Uploader
/// so updating / replacing the exe never loses settings.
/// </summary>
public static class AppPaths
{
    public const string AppName = "VRCX Image Discord Uploader";

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NekoSuneVR", AppName);

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string BackupsDir => Path.Combine(DataDir, "backups");

    public static string ExeDir => AppContext.BaseDirectory;
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(ExeDir, AppName + ".exe");

    public static string DefaultScreenshotDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");

    public static void Ensure()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(BackupsDir);
    }
}
