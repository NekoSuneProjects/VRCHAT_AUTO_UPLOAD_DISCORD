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
            UnprotectAll(s);
            FixupKinds(s);
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
                case 1:
                    // v1 had one bot: WebhookUrl/AuthKey/ServerId/ChannelId at the top.
                    // v2 has a list of destinations (community + own servers, each with its own key).
                    {
                        var url = Str(json, "WebhookUrl");
                        var keyProtected = Str(json, "AuthKeyProtected");
                        var plainKey = Str(json, "AuthKey"); // present in exported v1 backups
                        var list = new JsonArray();
                        if (url.Length > 0 || keyProtected.Length > 0 || plainKey.Length > 0)
                        {
                            list.Add(new JsonObject
                            {
                                ["Id"] = LegacyId,
                                ["Name"] = "My server",
                                ["Kind"] = (int)(url.Contains("nekumi", StringComparison.OrdinalIgnoreCase) ? DestinationKind.OwnServer : DestinationKind.Custom),
                                ["Url"] = url.Length > 0 ? url : UploadDestination.NekumiUploadUrl,
                                ["Enabled"] = true,
                                ["AuthKeyProtected"] = keyProtected,
                                ["ServerId"] = Str(json, "ServerId"),
                                ["ChannelId"] = Str(json, "ChannelId"),
                            });
                        }
                        json["Destinations"] = list;
                        foreach (var old in new[] { "WebhookUrl", "AuthKeyProtected", "ServerId", "ChannelId" })
                            json.Remove(old);
                    }
                    break;
                // case 2: json["NewName"] = json["OldName"]; json.Remove("OldName"); break;
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
        foreach (var d in s.Destinations) d.AuthKeyProtected = Protect(d.AuthKey);
        WriteAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(s, Json.Options));
    }

    // ---------- Export / import ----------

    /// <summary>Portable backup (auth key in plain text so it works on another PC).</summary>
    public static void Export(AppSettings s, string path)
    {
        var obj = JsonSerializer.SerializeToNode(s, Json.Options)!.AsObject();
        var list = obj["Destinations"]!.AsArray();
        for (int i = 0; i < list.Count; i++)
        {
            var d = list[i]!.AsObject();
            d.Remove("AuthKeyProtected");
            d["AuthKey"] = s.Destinations[i].AuthKey;
        }
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
        json.Remove("History");
        string legacyKey = Str(json, "AuthKey"); // v1 backups
        json = Migrate(json, version);

        // plain keys in backups, by destination index
        var plainKeys = (json["Destinations"] as JsonArray ?? new JsonArray())
            .Select(n => n is JsonObject o ? Str(o, "AuthKey") : "").ToList();

        var s = json.Deserialize<AppSettings>(Json.Options) ?? new AppSettings();
        UnprotectAll(s);
        for (int i = 0; i < s.Destinations.Count; i++)
        {
            if (i < plainKeys.Count && plainKeys[i].Length > 0) s.Destinations[i].AuthKey = plainKeys[i];
            else if (s.Destinations[i].Id == LegacyId && legacyKey.Length > 0) s.Destinations[i].AuthKey = legacyKey;
        }
        FixupKinds(s);

        if (includeHistory && history is JsonArray arr)
            UploadHistory.Instance.ReplaceAll(arr.Deserialize<List<HistoryEntry>>(Json.Options) ?? new());

        Save(s);
        return s;
    }

    /// <summary>Reads an old 1.x .env and adds it as a destination (or updates the one with the same key).</summary>
    public static void ImportEnv(string path, AppSettings s)
    {
        string url = "", key = "", server = "", channel = "";
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
                case "DISCORD_WEBHOOK_URL": url = v; break;
                case "DISCORD_WEBHOOK_AUTHKEY": key = v; break;
                case "SERVERID": server = v; break;
                case "CHANNELID": channel = v; break;
                case "VRCHAT_IMAGE_PATH": if (v.Length > 0) s.ScreenshotFolder = v; break;
            }
        }
        if (key.Length == 0 && url.Length == 0) return;

        var d = s.Destinations.FirstOrDefault(x => x.AuthKey.Trim() == key.Trim() && key.Length > 0);
        if (d == null)
        {
            d = new UploadDestination { Name = "My server" };
            s.Destinations.Add(d);
        }
        d.Url = url.Length > 0 ? url : UploadDestination.NekumiUploadUrl;
        d.AuthKey = key;
        d.ServerId = server;
        d.ChannelId = channel;
        d.Kind = d.Url.Contains("nekumi", StringComparison.OrdinalIgnoreCase) ? DestinationKind.OwnServer : DestinationKind.Custom;
        FixupKinds(s);
    }

    const string LegacyId = "legacy";

    /// <summary>A community member key (nkvrm_) on the Nekumi URL is the NekoSune Community preset.</summary>
    static void FixupKinds(AppSettings s)
    {
        foreach (var d in s.Destinations)
        {
            if (d.AuthKey.StartsWith("nkvrm_", StringComparison.Ordinal) && d.Kind != DestinationKind.Community
                && d.Url.Contains("nekumi", StringComparison.OrdinalIgnoreCase))
            {
                d.Kind = DestinationKind.Community;
                if (d.Name is "" or "My server") d.Name = UploadDestination.CommunityKey;
            }
        }
    }

    static void UnprotectAll(AppSettings s)
    {
        foreach (var d in s.Destinations) d.AuthKey = Unprotect(d.AuthKeyProtected);
    }

    static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var str) ? str : "";

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
