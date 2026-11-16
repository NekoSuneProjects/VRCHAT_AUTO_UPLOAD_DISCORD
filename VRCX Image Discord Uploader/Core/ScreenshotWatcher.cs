using System.Collections.Concurrent;
using System.IO;

namespace VRCX_Image_Discord_Uploader.Core;

/// <summary>
/// Watches the VRChat screenshot folder.
///
/// Why the old version failed: VRChat writes VRChat_..._1920x1080.png, then VRCX re-writes it with metadata
/// and renames it to ..._wrld_xxxx.png. The old code started one upload per event in parallel, so one upload
/// grabbed the original path right before it was renamed ("Could not find file") and the other read the file
/// before metadata was ready and sent "{}" (bot answers "Bad Request").
///
/// Now: every event for the same screenshot (Created / Changed / Renamed) is grouped by its base name and only
/// handled once the file has been quiet for SettleDelayMs. Uploads run one at a time, the final file name is
/// looked up fresh, and the bytes are read into memory once so later renames can't break the upload.
/// </summary>
public sealed class ScreenshotWatcher : IDisposable
{
    class Pending
    {
        public string Dir = "";
        public DateTime LastEvent;
        public DateTime FirstSeen;
    }

    readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _uploadLock = new(1, 1);
    FileSystemWatcher? _fsw;
    CancellationTokenSource? _cts;
    AppSettings _settings = new();

    public bool IsRunning => _fsw != null;
    public int QueueCount => _pending.Count;

    public event Action<bool>? RunningChanged;
    public event Action<HistoryEntry>? Processed;
    public event Action<string>? Activity;

    public bool Start(AppSettings settings)
    {
        Stop();
        _settings = settings.Clone();

        if (string.IsNullOrWhiteSpace(_settings.ScreenshotFolder) || !Directory.Exists(_settings.ScreenshotFolder))
        {
            Log.Error($"Screenshot folder not found: \"{_settings.ScreenshotFolder}\". Set it in Settings.");
            return false;
        }

        try
        {
            _fsw = new FileSystemWatcher(_settings.ScreenshotFolder, "*.png")
            {
                IncludeSubdirectories = _settings.IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
            };
            _fsw.Created += (_, e) => OnEvent(e.FullPath);
            _fsw.Changed += (_, e) => OnEvent(e.FullPath);
            _fsw.Renamed += (_, e) => OnEvent(e.FullPath);
            _fsw.Error += (_, e) => OnWatcherError(e.GetException());
            _fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not start watching: " + ex.Message);
            _fsw?.Dispose();
            _fsw = null;
            return false;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PumpAsync(_cts.Token));

        Log.Success($"Watching {_settings.ScreenshotFolder}");
        RunningChanged?.Invoke(true);
        return true;
    }

    public void Stop()
    {
        if (_fsw == null) return;
        _fsw.EnableRaisingEvents = false;
        _fsw.Dispose();
        _fsw = null;
        _cts?.Cancel();
        _cts = null;
        _pending.Clear();
        Log.Info("Stopped watching.");
        RunningChanged?.Invoke(false);
    }

    public void UpdateSettings(AppSettings s) => _settings = s.Clone();

    void OnWatcherError(Exception ex)
    {
        Log.Warn("File watcher error (" + ex.Message + "), restarting…");
        var s = _settings;
        Task.Delay(1000).ContinueWith(_ => Start(s));
    }

    void OnEvent(string path)
    {
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return;
        var baseName = ScreenshotMetadata.GetBaseName(path);
        if (!baseName.StartsWith("VRChat_", StringComparison.OrdinalIgnoreCase)) return;

        lock (_inFlight) if (_inFlight.Contains(baseName)) return;
        if (UploadHistory.Instance.WasUploaded(baseName)) return;

        var now = DateTime.Now;
        bool isNew = false;
        _pending.AddOrUpdate(baseName,
            _ => { isNew = true; return new Pending { Dir = Path.GetDirectoryName(path)!, LastEvent = now, FirstSeen = now }; },
            (_, p) => { p.LastEvent = now; return p; });

        if (isNew)
        {
            Log.Info($"Detected {Path.GetFileName(path)}");
            Activity?.Invoke("Waiting for VRCX to finish…");
        }
    }

    async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(400, ct);
                var now = DateTime.Now;
                var ready = _pending
                    .Where(kv => (now - kv.Value.LastEvent).TotalMilliseconds >= _settings.SettleDelayMs
                              || (now - kv.Value.FirstSeen).TotalSeconds >= 30) // safety cap
                    .OrderBy(kv => kv.Value.FirstSeen)
                    .ToList();

                foreach (var kv in ready)
                {
                    if (!_pending.TryRemove(kv.Key, out var p)) continue;
                    await ProcessAsync(kv.Key, p.Dir, null, null, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error("Watcher loop: " + ex.Message); }
        }
    }

    /// <summary>
    /// Upload a specific file by hand (from the dashboard), even if it was uploaded before.
    /// onlyDestinationIds limits it to some destinations (retrying the ones that failed).
    /// </summary>
    public Task<HistoryEntry?> UploadManualAsync(string path, IReadOnlyCollection<string>? onlyDestinationIds = null) =>
        ProcessAsync(ScreenshotMetadata.GetBaseName(path), Path.GetDirectoryName(path)!, path, onlyDestinationIds, CancellationToken.None);

    async Task<HistoryEntry?> ProcessAsync(string baseName, string dir, string? explicitPath, IReadOnlyCollection<string>? onlyIds, CancellationToken ct)
    {
        lock (_inFlight) if (!_inFlight.Add(baseName)) return null;
        await _uploadLock.WaitAsync(ct);
        try
        {
            if (explicitPath == null && UploadHistory.Instance.WasUploaded(baseName)) return null;

            var path = explicitPath ?? ResolveFinalFile(dir, baseName);
            if (path == null)
            {
                Log.Warn($"{baseName}: file disappeared before upload, skipped.");
                return null;
            }

            var (bytes, finalPath) = await ReadWhenReadyAsync(path, dir, baseName, ct);
            var info = _settings.UploadWithMetadata
                ? ScreenshotMetadata.Read(new MemoryStream(bytes), finalPath)
                : new ScreenshotInfo();

            // VRCX may still be about to rename – give it a moment if we have no world info yet.
            if (explicitPath == null && _settings.UploadWithMetadata && !info.HasWorld && !finalPath.Contains("_wrld_"))
            {
                for (int i = 0; i < 6 && !ct.IsCancellationRequested; i++)
                {
                    await Task.Delay(500, ct);
                    var renamed = ResolveFinalFile(dir, baseName);
                    if (renamed != null && renamed.Contains("_wrld_"))
                    {
                        (bytes, finalPath) = await ReadWhenReadyAsync(renamed, dir, baseName, ct);
                        info = ScreenshotMetadata.Read(new MemoryStream(bytes), finalPath);
                        break;
                    }
                }
            }

            Log.Info(info.HasWorld
                ? $"Uploading {Path.GetFileName(finalPath)} – {(string.IsNullOrEmpty(info.WorldName) ? info.WorldId : info.WorldName)}, {info.Players.Count} player(s) [{info.Source}]"
                : $"Uploading {Path.GetFileName(finalPath)} – no world info found");
            Activity?.Invoke("Uploading " + Path.GetFileName(finalPath));

            var targets = _settings.ActiveDestinations
                .Where(d => onlyIds == null || onlyIds.Contains(d.Id))
                .ToList();
            if (targets.Count == 0)
            {
                Log.Warn("No enabled upload destination with a key – add one in Settings → Upload destinations.");
                Activity?.Invoke("No upload destination set up");
                return null;
            }

            var payload = DiscordUploader.BuildPayload(info, _settings.IncludePlayers);
            var fileName = Path.GetFileName(finalPath);
            var results = new List<DestinationResult>();
            foreach (var d in targets)
            {
                var r = await DiscordUploader.UploadAsync(d, bytes, fileName, payload, ct);
                results.Add(new DestinationResult { DestinationId = d.Id, Name = d.Name, Success = r.Success, Error = r.Success ? "" : r.Message });
                if (r.Success) Log.Success($"Uploaded {fileName} → {d.Name}");
                else Log.Error($"Upload to {d.Name} failed for {fileName}: {r.Message}");
            }

            bool ok = results.All(r => r.Success);
            var entry = new HistoryEntry
            {
                BaseName = baseName,
                FilePath = finalPath,
                Time = DateTime.Now,
                Success = ok,
                WorldName = info.WorldName,
                WorldId = info.WorldId,
                PlayerCount = info.Players.Count,
                Results = results,
                Error = ok ? "" : string.Join("; ", results.Where(r => !r.Success).Select(r => $"{r.Name}: {r.Error}")),
            };
            UploadHistory.Instance.Add(entry);

            Processed?.Invoke(entry);
            Activity?.Invoke(ok ? "Idle" : "Last upload failed");
            return entry;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Error($"{baseName}: {ex.Message}");
            Activity?.Invoke("Error");
            return null;
        }
        finally
        {
            _uploadLock.Release();
            lock (_inFlight) _inFlight.Remove(baseName);
        }
    }

    /// <summary>Finds the current file for a screenshot, preferring VRCX's renamed "_wrld_" version.</summary>
    static string? ResolveFinalFile(string dir, string baseName)
    {
        if (!Directory.Exists(dir)) return null;
        var candidates = Directory.GetFiles(dir, baseName + "*.png")
            .Where(f =>
            {
                var n = Path.GetFileNameWithoutExtension(f);
                return n.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith(baseName + "_wrld_", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        return candidates.FirstOrDefault(f => f.Contains("_wrld_", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    /// <summary>Reads the whole file once it's no longer being written. Follows a rename if one happens meanwhile.</summary>
    static async Task<(byte[] Bytes, string Path)> ReadWhenReadyAsync(string path, string dir, string baseName, CancellationToken ct)
    {
        long lastLen = -1;
        for (int i = 0; i < 40; i++)
        {
            if (!File.Exists(path))
                path = ResolveFinalFile(dir, baseName) ?? throw new FileNotFoundException("Screenshot was moved or deleted.");

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                long len = fs.Length;
                if (len > 0 && len == lastLen)
                {
                    var buf = new byte[len];
                    await fs.ReadExactlyAsync(buf, ct);
                    return (buf, path);
                }
                lastLen = len;
            }
            catch (IOException) { /* still locked by VRChat / VRCX */ }

            await Task.Delay(400, ct);
        }
        throw new IOException("Screenshot stayed locked for too long.");
    }

    public void Dispose() => Stop();
}
