using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRCX_Image_Discord_Uploader.Core;

public class DestinationResult
{
    public string DestinationId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Success { get; set; }
    public string Error { get; set; } = "";
    public List<DestinationResult> Results { get; set; } = new();
}

public class HistoryEntry
{
    public string BaseName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime Time { get; set; }
    public bool Success { get; set; }
    public string WorldName { get; set; } = "";
    public string WorldId { get; set; } = "";
    public int PlayerCount { get; set; }
    public string Error { get; set; } = "";
    public List<DestinationResult> Results { get; set; } = new();

    [JsonIgnore] public string FileName => Path.GetFileName(FilePath);
    [JsonIgnore] public string TimeText => Time.ToString("dd MMM HH:mm:ss");
    [JsonIgnore] public string StatusText => Success ? "Uploaded" : Results.Any(r => r.Success) ? "Partly failed" : "Failed";
    [JsonIgnore] public string DestinationsText => Results.Count == 0 ? "" : string.Join("  ·  ", Results.Select(r => (r.Success ? "✓ " : "✗ ") + r.Name));
    [JsonIgnore] public string WorldText => string.IsNullOrEmpty(WorldName) ? (string.IsNullOrEmpty(WorldId) ? "No world info" : WorldId) : WorldName;
    [JsonIgnore] public string Detail => Success ? $"{PlayerCount} player(s) · {DestinationsText}" : Error;
}

public class UploadHistory
{
    public static UploadHistory Instance { get; } = new();

    readonly object _lock = new();
    List<HistoryEntry> _items = new();
    public int Limit { get; set; } = 500;

    public event Action? Changed;

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(AppPaths.HistoryFile))
                    _items = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(AppPaths.HistoryFile), Json.Options) ?? new();
            }
            catch (Exception ex)
            {
                Log.Warn("history.json unreadable, starting a new one: " + ex.Message);
                SettingsStore.BackupFile(AppPaths.HistoryFile, "history.corrupt");
                _items = new();
            }
        }
        Changed?.Invoke();
    }

    public bool WasUploaded(string baseName)
    {
        lock (_lock) return _items.Any(i => i.Success && i.BaseName.Equals(baseName, StringComparison.OrdinalIgnoreCase));
    }

    public void Add(HistoryEntry e)
    {
        lock (_lock)
        {
            // Same screenshot again (retry / re-upload): merge per-destination results into one entry.
            var prev = _items.FirstOrDefault(i => i.BaseName.Equals(e.BaseName, StringComparison.OrdinalIgnoreCase));
            if (prev != null)
            {
                var newIds = e.Results.Select(r => r.DestinationId).ToHashSet();
                e.Results = prev.Results.Where(r => !newIds.Contains(r.DestinationId)).Concat(e.Results).ToList();
                if (e.Results.Count > 0)
                {
                    e.Success = e.Results.All(r => r.Success);
                    e.Error = e.Success ? "" : string.Join("; ", e.Results.Where(r => !r.Success).Select(r => $"{r.Name}: {r.Error}"));
                }
                _items.Remove(prev);
            }
            _items.Insert(0, e);
            if (_items.Count > Limit) _items.RemoveRange(Limit, _items.Count - Limit);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public List<HistoryEntry> Snapshot() { lock (_lock) return _items.ToList(); }

    public void ReplaceAll(List<HistoryEntry> items)
    {
        lock (_lock) { _items = items; SaveLocked(); }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock) { _items.Clear(); SaveLocked(); }
        Changed?.Invoke();
    }

    void SaveLocked()
    {
        try
        {
            AppPaths.Ensure();
            SettingsStore.WriteAtomic(AppPaths.HistoryFile, JsonSerializer.Serialize(_items, Json.Options));
        }
        catch (Exception ex) { Log.Warn("Could not save history: " + ex.Message); }
    }
}
