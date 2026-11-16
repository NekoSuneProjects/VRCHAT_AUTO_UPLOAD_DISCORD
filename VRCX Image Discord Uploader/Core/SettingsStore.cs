using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VRCX_Image_Discord_Uploader.Core;

public static class SettingsStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NekoSuneVR.VRCXUploader");

    public static AppSettings Load()
    {
        AppPaths.Ensure();

        if (!File.Exists(AppPaths.SettingsFile))
        {
            var fresh = new AppSettings();
            // First run: pull in an old .env sitting next to the exe so nobody has to retype it.
            var env = Path.Combine(AppPaths.ExeDir, ".env");
            if (File.Exists(env))
            {
                ImportEnv(env, fresh);
                Log.Info("Imported settings from legacy .env file.");
            }
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile)) as JsonObject
                       ?? throw new InvalidDataException("not a JSON object");
            int version = GetInt(json, "SchemaVersion");
            if (version < AppSettings.CurrentSchema)
            {
                BackupFile(AppPaths.SettingsFile, $"settings.v{version}");
                json = Migrate(json, version);
            }

            var s = json.Deserialize<AppSettings>(Json.Options) ?? new AppSettings();
            s.AuthKey = Unprotect(s.AuthKeyProtected);
            if (version < AppSettings.CurrentSchema) Save(s);
            return s;
        }
        catch (Exception ex)
        {
            Log.Error("settings.json is damaged, backing it up and starting fresh: " + ex.Message);
            BackupFile(AppPaths.SettingsFile, "settings.corrupt");
            var s = new AppSettings();
            Save(s);
            return s;
        }
    }

    /// <summary>
    /// Upgrade older settings files step by step. When the layout changes:
    /// bump AppSettings.CurrentSchema and add a "case N:" that converts N -> N+1.
    /// </summary>
    static JsonObject Migrate(JsonObject json, int from)
    {
        for (int v = from; v < AppSettings.CurrentSchema; v++)
        {
            switch (v)
            {
                case 0:
                    // v0 = unversioned file; nothing to rename yet, just stamp it.
                    break;
                // case 1: json["NewName"] = json["OldName"]; json.Remove("OldName"); break;
            }
            Log.Info($"Migrated settings schema v{v} -> v{v + 1}");
        }
        json["SchemaVersion"] = AppSettings.CurrentSchema;
        return json;
    }

    public static void Save(AppSettings s)
    {
        AppPaths.Ensure();
        s.SchemaVersion = AppSettings.CurrentSchema;
        s.AuthKeyProtected = Protect(s.AuthKey);
        WriteAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(s, Json.Options));
    }

    // ---------- Export / import ----------

    /// <summary>Portable backup (auth key in plain text so it works on another PC).</summary>
    public static void Export(AppSettings s, string path)
    {
        var obj = JsonSerializer.SerializeToNode(s, Json.Options)!.AsObject();
        obj.Remove("AuthKeyProtected");
        obj["AuthKey"] = s.AuthKey;
        obj["ExportedAt"] = DateTime.UtcNow.ToString("o");
        obj["History"] = JsonSerializer.SerializeToNode(UploadHistory.Instance.Snapshot(), Json.Options);
        File.WriteAllText(path, obj.ToJsonString(Json.Options));
    }

    public static AppSettings Import(string path, bool includeHistory)
    {
        var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new InvalidDataException("This is not a backup file.");
        int version = GetInt(json, "SchemaVersion");
        var history = json["History"];
        string key = json["AuthKey"] is JsonValue kv && kv.TryGetValue<string>(out var k) ? k : "";
        json.Remove("History");
        json.Remove("AuthKey");
        json = Migrate(json, version);

        var s = json.Deserialize<AppSettings>(Json.Options) ?? new AppSettings();
        s.AuthKey = !string.IsNullOrEmpty(key) ? key : Unprotect(s.AuthKeyProtected);

        if (includeHistory && history is JsonArray arr)
            UploadHistory.Instance.ReplaceAll(arr.Deserialize<List<HistoryEntry>>(Json.Options) ?? new());

        Save(s);
        return s;
    }

    public static void ImportEnv(string path, AppSettings s)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var k = line[..eq].Trim().ToUpperInvariant();
            var v = line[(eq + 1)..].Trim().Trim('"', '\'');
            switch (k)
            {
                case "DISCORD_WEBHOOK_URL": s.WebhookUrl = v; break;
                case "DISCORD_WEBHOOK_AUTHKEY": s.AuthKey = v; break;
                case "SERVERID": s.ServerId = v; break;
                case "CHANNELID": s.ChannelId = v; break;
                case "VRCHAT_IMAGE_PATH": s.ScreenshotFolder = v; break;
            }
        }
    }

    public static void ResetAll()
    {
        BackupFile(AppPaths.SettingsFile, "settings.before-reset");
        BackupFile(AppPaths.HistoryFile, "history.before-reset");
        if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
        UploadHistory.Instance.Clear();
    }

    // ---------- helpers ----------

    public static void BackupFile(string file, string label)
    {
        try
        {
            if (!File.Exists(file)) return;
            AppPaths.Ensure();
            var dest = Path.Combine(AppPaths.BackupsDir, $"{label}.{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(file, dest, true);
        }
        catch (Exception ex) { Log.Warn("Backup failed: " + ex.Message); }
    }

    public static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    static int GetInt(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var bytes = Dpapi.Protect(Encoding.UTF8.GetBytes(plain), Entropy);
            return Convert.ToBase64String(bytes);
        }
        catch { return ""; }
    }

    static string Unprotect(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return "";
        try
        {
            var bytes = Dpapi.Unprotect(Convert.FromBase64String(b64), Entropy);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            Log.Warn("Could not decrypt saved auth key (different Windows user/PC?). Please re-enter it.");
            return "";
        }
    }
}
