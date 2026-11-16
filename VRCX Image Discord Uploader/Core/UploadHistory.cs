using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRCX_Image_Discord_Uploader.Core;

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

    [JsonIgnore] public string FileName => Path.GetFileName(FilePath);
    [JsonIgnore] public string TimeText => Time.ToString("dd MMM HH:mm:ss");
    [JsonIgnore] public string StatusText => Success ? "Uploaded" : "Failed";
    [JsonIgnore] public string WorldText => string.IsNullOrEmpty(WorldName) ? (string.IsNullOrEmpty(WorldId) ? "No world info" : WorldId) : WorldName;
    [JsonIgnore] public string Detail => Success ? $"{WorldText} · {PlayerCount} player(s)" : Error;
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
            // a later success replaces earlier failures for the same screenshot
            if (e.Success) _items.RemoveAll(i => !i.Success && i.BaseName.Equals(e.BaseName, StringComparison.OrdinalIgnoreCase));
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
