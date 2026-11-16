using System.IO;

namespace VRCX_Image_Discord_Uploader.Core;

public enum LogLevel { Info, Success, Warn, Error }

public record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => Level.ToString().ToUpperInvariant();
}

public static class Log
{
    static readonly object Lock = new();
    public static event Action<LogEntry>? Written;

    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Success(string m) => Write(LogLevel.Success, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m) => Write(LogLevel.Error, m);

    public static void Write(LogLevel level, string message)
    {
        var e = new LogEntry(DateTime.Now, level, message);
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                File.AppendAllText(Path.Combine(AppPaths.LogsDir, $"{e.Time:yyyy-MM-dd}.log"),
                    $"[{e.Time:HH:mm:ss}] {e.LevelText,-7} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
        Written?.Invoke(e);
    }

    public static void CleanupOld(int keepDays = 14)
    {
        try
        {
            foreach (var f in Directory.GetFiles(AppPaths.LogsDir, "*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-keepDays)) File.Delete(f);
        }
        catch { }
    }
}
