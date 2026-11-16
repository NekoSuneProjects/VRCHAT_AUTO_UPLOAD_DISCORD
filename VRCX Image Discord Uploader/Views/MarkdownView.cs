using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace VRCX_Image_Discord_Uploader.Views;

/// <summary>Tiny markdown renderer for changelogs: ## / ### headings, - bullets (nested), **bold**, `code`, [links](url).</summary>
public static partial class MarkdownView
{
    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`|\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex InlineRegex();

    [GeneratedRegex(@"^(\s*)[-*]\s+(.*)$")]
    private static partial Regex BulletRegex();

    public static void Render(Panel target, string markdown)
    {
        target.Children.Clear();
        var text = (Brush)Application.Current.FindResource("Text");
        var muted = (Brush)Application.Current.FindResource("TextMuted");
        var accent = (Brush)Application.Current.FindResource("Accent");

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith("<!--")) continue;

            if (line.StartsWith("# ")) continue; // file title
            if (line.StartsWith("## "))
            {
                target.Children.Add(Block(line[3..], 17, FontWeights.Bold, accent, new Thickness(0, target.Children.Count == 0 ? 0 : 18, 0, 6)));
                continue;
            }
            if (line.StartsWith("### "))
            {
                target.Children.Add(Block(line[4..].ToUpperInvariant(), 11.5, FontWeights.SemiBold, muted, new Thickness(0, 10, 0, 4)));
                continue;
            }

            if (line.StartsWith("> "))
            {
                var q = InlineText(line[2..], muted);
                q.FontStyle = FontStyles.Italic;
                q.Margin = new Thickness(0, 2, 0, 6);
                target.Children.Add(q);
                continue;
            }
            if (line == "---") continue;

            var b = BulletRegex().Match(line);
            if (b.Success)
            {
                int depth = b.Groups[1].Value.Replace("\t", "  ").Length / 2;
                var grid = new Grid { Margin = new Thickness(depth * 18, 2, 0, 2) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.Children.Add(new TextBlock { Text = depth == 0 ? "•" : "◦", Foreground = accent, FontSize = 13 });
                var tb = InlineText(b.Groups[2].Value, text);
                Grid.SetColumn(tb, 1);
                grid.Children.Add(tb);
                target.Children.Add(grid);
                continue;
            }

            var p = InlineText(line.Trim(), text);
            p.Margin = new Thickness(0, 2, 0, 4);
            target.Children.Add(p);
        }

        if (target.Children.Count == 0)
            target.Children.Add(Block("No changelog was published for this version.", 13, FontWeights.Normal, muted, new Thickness(0)));
    }

    static TextBlock Block(string s, double size, FontWeight weight, Brush color, Thickness margin) =>
        new() { Text = s, FontSize = size, FontWeight = weight, Foreground = color, Margin = margin, TextWrapping = TextWrapping.Wrap };

    static TextBlock InlineText(string s, Brush color)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = color, FontSize = 13, LineHeight = 19 };
        int pos = 0;
        foreach (Match m in InlineRegex().Matches(s))
        {
            if (m.Index > pos) tb.Inlines.Add(new Run(s[pos..m.Index]));
            if (m.Groups[1].Success) tb.Inlines.Add(new Bold(new Run(m.Groups[1].Value)));
            else if (m.Groups[2].Success)
                tb.Inlines.Add(new Run(m.Groups[2].Value) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)) });
            else
            {
                var url = m.Groups[4].Value;
                var link = new Hyperlink(new Run(m.Groups[3].Value)) { Foreground = (Brush)Application.Current.FindResource("Accent") };
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http"))
                {
                    link.NavigateUri = uri;
                    link.RequestNavigate += (_, e) =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                        e.Handled = true;
                    };
                }
                tb.Inlines.Add(link);
            }
            pos = m.Index + m.Length;
        }
        if (pos < s.Length) tb.Inlines.Add(new Run(s[pos..]));
        return tb;
    }
}
