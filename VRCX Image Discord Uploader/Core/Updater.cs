using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;

namespace VRCX_Image_Discord_Uploader.Core;

/// <summary>
/// Downloads a release zip and swaps the program files once the app has closed.
/// Settings/history live in AppData, so they are never touched.
/// </summary>
public static class Updater
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.TryParseAdd("VRCX-Image-Discord-Uploader");
        return c;
    }

    /// <summary>Can we replace files next to the exe? (Not if it was put in Program Files.)</summary>
    public static bool CanSelfUpdate()
    {
        try
        {
            var probe = Path.Combine(AppPaths.ExeDir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Downloads + unpacks the release. Returns the folder with the new files.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (release.ZipUrl == null) throw new InvalidOperationException("This release has no zip to download.");

        var work = Path.Combine(Path.GetTempPath(), "VRCXUploaderUpdate");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, "update.zip");
        var extract = Path.Combine(work, "files");

        using (var resp = await Http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? release.ZipSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        ZipFile.ExtractToDirectory(zipPath, extract);

        // Zip may contain the files at the root or inside one folder.
        var exeName = Path.GetFileName(AppPaths.ExePath);
        var root = Directory.GetFiles(extract, exeName, SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .OrderBy(d => d!.Length)
            .FirstOrDefault()
            ?? throw new InvalidDataException($"The download doesn't contain {exeName}.");
        return root!;
    }

    /// <summary>
    /// Writes a small script that waits for this process to exit, copies the new files over the
    /// install folder, and starts the app again. Caller must shut the app down right after.
    /// </summary>
    public static void LaunchInstaller(string newFilesDir)
    {
        var target = AppPaths.ExeDir.TrimEnd('\\', '/');
        var exe = AppPaths.ExePath;
        var pid = Environment.ProcessId;
        var log = Path.Combine(AppPaths.LogsDir, "update.log");
        var script = Path.Combine(Path.GetTempPath(), "VRCXUploaderUpdate", "install.cmd");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine($"echo [%date% %time%] Updating \"{target}\" >> \"{log}\"");
        // Wait (max ~60s) for the app to close. No ( ) block here: %vars% inside a block are expanded once.
        // "ping" is the sleep because "timeout" fails without a console window.
        sb.AppendLine("set /a tries=0");
        sb.AppendLine(":wait");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" /FO CSV /NH 2>nul | find \"\"\"{pid}\"\"\" >nul");
        sb.AppendLine("if errorlevel 1 goto copy");
        sb.AppendLine("set /a tries+=1");
        sb.AppendLine("if %tries% geq 60 goto copy");
        sb.AppendLine("ping -n 2 127.0.0.1 >nul");
        sb.AppendLine("goto wait");
        sb.AppendLine(":copy");
        // robocopy exit codes < 8 mean success; never touches files that only exist in the target
        sb.AppendLine($"robocopy \"{newFilesDir}\" \"{target}\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >> \"{log}\"");
        sb.AppendLine("if errorlevel 8 (");
        sb.AppendLine($"  echo [%date% %time%] Copy failed >> \"{log}\"");
        sb.AppendLine(") else (");
        sb.AppendLine($"  echo [%date% %time%] Update installed >> \"{log}\"");
        sb.AppendLine(")");
        sb.AppendLine($"start \"\" \"{exe}\"");
        sb.AppendLine("exit");

        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));
        Log.Info("Starting updater, the app will restart.");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }
}
