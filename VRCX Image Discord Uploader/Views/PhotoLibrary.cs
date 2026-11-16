using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VRCX_Image_Discord_Uploader.Core;

namespace VRCX_Image_Discord_Uploader.Views;

public enum PhotoStatus { NotUploaded, Uploaded, Partial, Failed }

/// <summary>One screenshot in the Photos page. Thumbnail loads in the background on first use.</summary>
public sealed class PhotoItem : INotifyPropertyChanged
{
    static readonly SemaphoreSlim ThumbGate = new(3, 3);

    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string BaseName { get; }
    public DateTime TakenAt { get; }
    public string TimeText => TakenAt.ToString("ddd d MMM · HH:mm");

    public PhotoItem(string path)
    {
        Path = path;
        BaseName = ScreenshotMetadata.GetBaseName(path);
        TakenAt = ScreenshotMetadata.TakenAt(path);
        RefreshStatus();
    }

    PhotoStatus _status;
    public PhotoStatus Status { get => _status; private set { _status = value; Raise(); Raise(nameof(StatusText)); } }
    public string StatusText => Status switch
    {
        PhotoStatus.Uploaded => "Uploaded",
        PhotoStatus.Partial => "Partly uploaded",
        PhotoStatus.Failed => "Failed",
        _ => "Not uploaded",
    };

    public void RefreshStatus()
    {
        var h = UploadHistory.Instance.Find(BaseName);
        Status = h == null ? PhotoStatus.NotUploaded
            : h.Success ? PhotoStatus.Uploaded
            : h.Results.Any(r => r.Success) ? PhotoStatus.Partial
            : PhotoStatus.Failed;
    }

    ImageSource? _thumb;
    bool _requested;
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                _ = LoadThumbAsync();
            }
            return _thumb;
        }
    }

    async Task LoadThumbAsync()
    {
        await ThumbGate.WaitAsync();
        try
        {
            var img = await Task.Run(() => Load(Path, 320));
            _thumb = img;
            Raise(nameof(Thumbnail));
        }
        finally { ThumbGate.Release(); }
    }

    /// <summary>Decodes a downscaled, frozen copy (doesn't keep the file locked). Safe off the UI thread.</summary>
    public static ImageSource? Load(string path, int width)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = width;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class PhotoMonth
{
    public DateTime Month { get; init; }
    public List<string> Files { get; init; } = new();
    public string Title => Month.ToString(Month.Year == DateTime.Now.Year ? "MMMM" : "MMMM yyyy");
    public string Sub => Month.Year == DateTime.Now.Year ? Month.Year.ToString() : "";
    public int Count => Files.Count;
}

public static class PhotoLibrary
{
    /// <summary>All VRChat screenshots under the folder, grouped by the month they were taken (newest first).</summary>
    public static List<PhotoMonth> Scan(string folder, bool includeSubfolders)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new();
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        return Directory.EnumerateFiles(folder, "VRChat_*.png", opts)
            .Select(f => (File: f, Taken: ScreenshotMetadata.TakenAt(f)))
            .GroupBy(x => new DateTime(x.Taken.Year, x.Taken.Month, 1))
            .OrderByDescending(g => g.Key)
            .Select(g => new PhotoMonth
            {
                Month = g.Key,
                Files = g.OrderByDescending(x => x.Taken).Select(x => x.File).ToList(),
            })
            .ToList();
    }
}
