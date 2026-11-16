using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace VRCX_Image_Discord_Uploader.Core;

public record Player(string Id, string DisplayName);

public class ScreenshotInfo
{
    public string WorldName { get; set; } = "";
    public string WorldId { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public List<Player> Players { get; } = new();
    public string Source { get; set; } = "none";
    public bool HasWorld => !string.IsNullOrEmpty(WorldId) || !string.IsNullOrEmpty(WorldName);
}

/// <summary>
/// Reads world/player info from a VRChat screenshot. Tries, in order:
///  1. VRCX JSON written into a PNG text chunk ("Description")
///  2. VRChat's own XMP metadata (vrc:WorldID / vrc:WorldDisplayName)
///  3. The "_wrld_xxxx" part VRCX adds to the file name
/// </summary>
public static partial class ScreenshotMetadata
{
    [GeneratedRegex(@"_(wrld_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})")]
    private static partial Regex FileWorldRegex();

    [GeneratedRegex(@"<vrc:WorldID>\s*([^<]+?)\s*</vrc:WorldID>|vrc:WorldID=""([^""]+)""")]
    private static partial Regex XmpWorldIdRegex();

    [GeneratedRegex(@"<vrc:WorldDisplayName>\s*([^<]+?)\s*</vrc:WorldDisplayName>|vrc:WorldDisplayName=""([^""]+)""")]
    private static partial Regex XmpWorldNameRegex();

    /// <summary>Strips VRCX's "_wrld_..." suffix so the original and renamed file share one key.</summary>
    public static string GetBaseName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        int i = name.IndexOf("_wrld_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? name[..i] : name;
    }

    public static string? WorldIdFromFileName(string path)
    {
        var m = FileWorldRegex().Match(Path.GetFileName(path));
        return m.Success ? m.Groups[1].Value : null;
    }

    public static ScreenshotInfo Read(Stream png, string fileName)
    {
        var info = new ScreenshotInfo();
        var texts = new List<string>();

        try
        {
            foreach (var (keyword, value) in PngText.ReadAll(png))
                texts.Add(keyword + ": " + value);
        }
        catch (Exception ex)
        {
            Log.Warn($"Metadata read failed for {Path.GetFileName(fileName)}: {ex.Message}");
        }

        foreach (var t in texts)
            if (TryParseVrcx(t, info)) { info.Source = "VRCX"; break; }

        if (!info.HasWorld)
            foreach (var t in texts)
                if (TryParseXmp(t, info)) { info.Source = "VRChat XMP"; break; }

        if (string.IsNullOrEmpty(info.WorldId))
        {
            var id = WorldIdFromFileName(fileName);
            if (id != null)
            {
                info.WorldId = id;
                if (info.Source == "none") info.Source = "file name";
            }
        }

        return info;
    }

    static string StripKeyword(string text)
    {
        // MetadataExtractor gives "Keyword: value"
        int i = text.IndexOf(": ", StringComparison.Ordinal);
        if (i > 0 && i < 40 && !text[..i].Contains('{')) return text[(i + 2)..].Trim();
        return text.Trim();
    }

    static bool TryParseVrcx(string text, ScreenshotInfo info)
    {
        var body = StripKeyword(text);
        int start = body.IndexOf('{');
        if (start < 0) return false;
        try
        {
            if (JsonNode.Parse(body[start..]) is not JsonObject j) return false;
            if (j["world"] is not JsonObject world) return false;

            info.WorldName = Str(world, "name");
            info.WorldId = Str(world, "id");
            // VRCX: "instanceId": "wrld_xxx:12345~friends(usr_...)"
            var inst = Str(world, "instanceId");
            int c = inst.IndexOf(':');
            if (string.IsNullOrEmpty(info.WorldId)) info.WorldId = c > 0 ? inst[..c] : inst;
            if (c > 0) info.InstanceId = SafeInstance(inst[(c + 1)..]);

            if (j["players"] is JsonArray players)
                foreach (var p in players.OfType<JsonObject>())
                {
                    var name = Str(p, "displayName");
                    if (!string.IsNullOrEmpty(name))
                        info.Players.Add(new Player(Str(p, "id"), name));
                }
            return info.HasWorld;
        }
        catch { return false; }
    }

    /// <summary>
    /// "12345~private(usr_x)~canRequestInvite~region(eu)~nonce(secret)" -> "12345~private~canRequestInvite~region(eu)".
    /// The nonce would let anyone join a private instance, and owner IDs aren't needed in a public post.
    /// </summary>
    public static string SafeInstance(string instance)
    {
        var parts = instance.Split('~', StringSplitOptions.RemoveEmptyEntries);
        var keep = new List<string>();
        foreach (var p in parts)
        {
            int paren = p.IndexOf('(');
            var name = paren >= 0 ? p[..paren] : p;
            if (name.Equals("nonce", StringComparison.OrdinalIgnoreCase)) continue;
            keep.Add(name.Equals("region", StringComparison.OrdinalIgnoreCase) ? p : name);
        }
        return string.Join("~", keep);
    }

    static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static bool TryParseXmp(string text, ScreenshotInfo info)
    {
        if (!text.Contains("vrc:", StringComparison.Ordinal)) return false;
        var id = XmpWorldIdRegex().Match(text);
        var name = XmpWorldNameRegex().Match(text);
        if (id.Success) info.WorldId = (id.Groups[1].Success ? id.Groups[1] : id.Groups[2]).Value;
        if (name.Success) info.WorldName = (name.Groups[1].Success ? name.Groups[1] : name.Groups[2]).Value;
        return info.HasWorld;
    }
}
