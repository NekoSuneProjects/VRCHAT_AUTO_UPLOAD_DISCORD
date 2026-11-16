using System.IO;
using System.Text.RegularExpressions;

namespace VRCX_Image_Discord_Uploader.Core;

/// <summary>
/// Reads CHANGELOG.md ("## 2.2.0" sections). A copy is embedded in the exe so the app can show
/// "What's new" for the version it just updated to, even offline.
/// </summary>
public static partial class Changelog
{
    public record Section(AppVersion Version, string Title, string Body);

    // "## 2.2.0", "## v2.2.0", "## [2.2.0] - 2026-11-16"
    [GeneratedRegex(@"^##\s+\[?v?(\d+\.\d+\.\d+[^\]\s]*)\]?(.*)$")]
    private static partial Regex HeadingRegex();

    public static List<Section> Parse(string markdown)
    {
        var result = new List<Section>();
        string? title = null;
        AppVersion? version = null;
        var body = new List<string>();

        void Flush()
        {
            if (version != null) result.Add(new Section(version.Value, title!, string.Join("\n", body).Trim()));
            body.Clear();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var m = HeadingRegex().Match(raw.TrimEnd());
            if (m.Success)
            {
                Flush();
                version = AppVersion.Parse(m.Groups[1].Value);
                title = m.Groups[1].Value;
                continue;
            }
            if (raw.StartsWith("## ")) { Flush(); version = null; continue; } // non-version section
            if (version != null) body.Add(raw);
        }
        Flush();
        return result;
    }

    /// <summary>The CHANGELOG.md built into this exe.</summary>
    public static string Embedded()
    {
        try
        {
            var asm = typeof(Changelog).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("CHANGELOG.md", StringComparison.OrdinalIgnoreCase));
            if (name == null) return "";
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch { return ""; }
    }

    /// <summary>Notes for one version (betas use their base version's section).</summary>
    public static string? For(AppVersion v, string markdown) =>
        Parse(markdown).FirstOrDefault(s => (s.Version.Major, s.Version.Minor, s.Version.Patch) == (v.Major, v.Minor, v.Patch))?.Body;
}
