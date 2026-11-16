using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using VRCX_Image_Discord_Uploader.Core;
using VRCX_Image_Discord_Uploader.Views;
using Forms = System.Windows.Forms;

namespace VRCX_Image_Discord_Uploader;

public class HistoryItem
{
    public HistoryEntry Entry { get; }
    public HistoryItem(HistoryEntry e) => Entry = e;

    ImageSource? _thumb;
    bool _loaded;
    public ImageSource? Thumbnail
    {
        get
        {
            if (_loaded) return _thumb;
            _loaded = true;
            try
            {
                if (File.Exists(Entry.FilePath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
                    bmp.DecodePixelWidth = 224;
                    bmp.UriSource = new Uri(Entry.FilePath);
                    bmp.EndInit();
                    bmp.Freeze();
                    _thumb = bmp;
                }
            }
            catch { }
            return _thumb;
        }
    }
}

public partial class MainWindow : Window
{
    AppSettings _settings;
    readonly ScreenshotWatcher _watcher = new();
    readonly ObservableCollection<HistoryItem> _recent = new();
    readonly ObservableCollection<LogEntry> _logs = new();
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem? _trayToggle;
    bool _quitting;
    bool _trayHintShown;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        VersionText.Text = "v" + AppVersion.Current;
        RecentList.ItemsSource = _recent;
        LogList.ItemsSource = _logs;

        Log.Written += e => Dispatcher.BeginInvoke(() => AddLog(e));
        UploadHistory.Instance.Changed += () => Dispatcher.BeginInvoke(RefreshHistory);
        _watcher.RunningChanged += r => Dispatcher.BeginInvoke(() => SetRunningUi(r));
        _watcher.Activity += a => Dispatcher.BeginInvoke(() => ActivityText.Text = a);
        _watcher.Processed += e => Dispatcher.BeginInvoke(() => Notify(e));
        // new photo uploaded while the Photos page is open: rescan so it shows up
        _watcher.Processed += entry => Dispatcher.BeginInvoke(() =>
        {
            if (PagePhotos.Visibility == Visibility.Visible && PhotoDetail.Visibility != Visibility.Visible) _ = LoadPhotosAsync();
        });

        StateChanged += (_, _) =>
        {
            RootBorder.Padding = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };

        LoadSettingsIntoUi();
        RefreshHistory();
        RefreshDataPage();
        SetupTray();
        UpdateSetupBanner();

        // update check shortly after start, then every 6 hours
        _updateTimer.Interval = TimeSpan.FromHours(6);
        _updateTimer.Tick += async (_, _) =>
        {
            await CheckUpdatesAsync(manual: false);
            await RefreshDestinationInfoAsync();
        };
        _updateTimer.Start();
        _ = FirstUpdateCheckAsync();
        ShowWhatsNewAfterUpdate();

        if (_settings.StartWatchingOnLaunch && IsConfigured(out _))
            _watcher.Start(_settings);
    }

    // ================= Window / tray =================

    public void StartHidden()
    {
        // Show+Hide once so the window handle exists, then live in the tray.
        WindowState = WindowState.Minimized;
        Show();
        Hide();
        WindowState = WindowState.Normal;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
    }

    void SetupTray()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            _tray = new Forms.NotifyIcon
            {
                Icon = new System.Drawing.Icon(info.Stream),
                Text = "VRCX Image Discord Uploader",
                Visible = true,
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
            _trayToggle = new Forms.ToolStripMenuItem("Start watching", null, (_, _) => Dispatcher.Invoke(ToggleWatching));
            menu.Items.Add(_trayToggle);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Quit));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        }
        catch (Exception ex) { Log.Warn("Tray icon unavailable: " + ex.Message); }
    }

    void Notify(HistoryEntry e)
    {
        if (_tray == null || !_settings.ShowNotifications) return;
        if (e.Success)
            _tray.ShowBalloonTip(3000, "Screenshot uploaded", e.WorldText, Forms.ToolTipIcon.None);
        else
            _tray.ShowBalloonTip(5000, "Upload failed", e.Error, Forms.ToolTipIcon.Warning);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_quitting && _settings.MinimizeToTray && _tray != null)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(3000, "Still running", "VRCX Uploader keeps working in the tray. Right-click the icon to quit.", Forms.ToolTipIcon.None);
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Quit();
    }

    void Quit()
    {
        if (_quitting && Application.Current == null) return;
        _quitting = true;
        _watcher.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        Application.Current?.Shutdown();
    }

    void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object s, RoutedEventArgs e) => Close();

    // ================= Navigation =================

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageDashboard == null) return; // during InitializeComponent
        var tag = (string)((RadioButton)sender).Tag;
        PageDashboard.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PageData.Visibility = tag == "Data" ? Visibility.Visible : Visibility.Collapsed;
        PageLogs.Visibility = tag == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        PagePhotos.Visibility = tag == "Photos" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Data") RefreshDataPage();
        if (tag == "Photos") _ = LoadPhotosAsync();
        if (tag == "Logs" && _logs.Count > 0) LogList.ScrollIntoView(_logs[^1]);
    }

    void GoSettings_Click(object s, RoutedEventArgs e) => NavSettings.IsChecked = true;

    // ================= Dashboard =================

    void Toggle_Click(object s, RoutedEventArgs e) => ToggleWatching();

    void ToggleWatching()
    {
        if (_watcher.IsRunning) { _watcher.Stop(); return; }
        if (!IsConfigured(out var why))
        {
            Toast(why, error: true);
            NavSettings.IsChecked = true;
            ShowFromTray();
            return;
        }
        if (!_watcher.Start(_settings)) Toast("Could not start – see Logs", error: true);
    }

    void SetRunningUi(bool running)
    {
        var brush = (Brush)FindResource(running ? "Success" : "TextDim");
        StatusDot.Fill = SideDot.Fill = brush;
        StatusText.Text = SideStatus.Text = running ? "Watching" : "Stopped";
        ToggleText.Text = running ? "Stop watching" : "Start watching";
        ToggleIcon.Text = running ? "" : "";
        ActivityText.Text = running ? "Idle – waiting for new photos" : "Not watching";
        if (_trayToggle != null) _trayToggle.Text = running ? "Stop watching" : "Start watching";
        if (_tray != null) _tray.Text = "VRCX Image Discord Uploader – " + (running ? "watching" : "stopped");
    }

    void RefreshHistory()
    {
        foreach (var p in _monthItems) p.RefreshStatus();
        if (PhotoDetail.Visibility == Visibility.Visible && _detail != null) RenderDetailDestinations();
        var all = UploadHistory.Instance.Snapshot();
        _recent.Clear();
        foreach (var h in all.Take(100)) _recent.Add(new HistoryItem(h));
        EmptyState.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        StatToday.Text = all.Count(h => h.Success && h.Time.Date == DateTime.Today).ToString();
        StatTotal.Text = all.Count(h => h.Success).ToString();
        StatFailed.Text = all.Count(h => !h.Success).ToString();
        DataHistoryCount.Text = all.Count.ToString();
    }

    /// <summary>"Other file…": open any PNG in the photo view, where the servers can be picked.</summary>
    void UploadFile_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "PNG screenshots|*.png",
            InitialDirectory = Directory.Exists(_settings.ScreenshotFolder) ? _settings.ScreenshotFolder : "",
        };
        if (dlg.ShowDialog(this) != true) return;
        ShowPhotoDetail(new PhotoItem(dlg.FileName), standalone: true);
    }

    // ================= Photos =================

    List<PhotoMonth> _months = new();
    List<PhotoItem> _monthItems = new();
    List<PhotoItem> _visiblePhotos = new();
    PhotoItem? _detail;
    bool _detailStandalone, _scanning;
    HashSet<string> _detailOnServer = new(); // destination ids whose server already has this photo

    async Task LoadPhotosAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var folder = _settings.ScreenshotFolder;
            if (!Directory.Exists(folder))
            {
                PhotosSub.Text = "Pick your VRChat screenshot folder in Settings first.";
                MonthList.ItemsSource = null;
                ShowPhotos(new List<PhotoItem>());
                return;
            }
            PhotosSub.Text = "Scanning…";
            var keep = (MonthList.SelectedItem as PhotoMonth)?.Month;
            _months = await Task.Run(() => PhotoLibrary.Scan(folder, _settings.IncludeSubfolders));
            int total = _months.Sum(m => m.Count);
            PhotosSub.Text = $"{total:N0} photos in {folder}";
            MonthList.ItemsSource = _months;
            MonthList.SelectedItem = _months.FirstOrDefault(m => m.Month == keep) ?? _months.FirstOrDefault();
            if (_months.Count == 0) ShowPhotos(new List<PhotoItem>());
        }
        catch (Exception ex)
        {
            PhotosSub.Text = "Couldn't read the folder: " + ex.Message;
        }
        finally { _scanning = false; }
    }

    void MonthList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (MonthList.SelectedItem is not PhotoMonth m) return;
        _monthItems = m.Files.Select(f => new PhotoItem(f)).ToList();
        PhotoScroll.ScrollToTop();
        ApplyPhotoFilter();
    }

    void PhotoFilter_Checked(object s, RoutedEventArgs e)
    {
        if (PhotoGrid != null) ApplyPhotoFilter();
    }

    void ApplyPhotoFilter()
    {
        IEnumerable<PhotoItem> list = _monthItems;
        if (FilterNot.IsChecked == true) list = list.Where(p => p.Status == PhotoStatus.NotUploaded);
        else if (FilterDone.IsChecked == true) list = list.Where(p => p.Status != PhotoStatus.NotUploaded);
        ShowPhotos(list.ToList());
    }

    void ShowPhotos(List<PhotoItem> list)
    {
        _visiblePhotos = list;
        PhotoGrid.ItemsSource = list;
        int done = _monthItems.Count(p => p.Status == PhotoStatus.Uploaded);
        PhotoCount.Text = _monthItems.Count == 0 ? "" : $"{list.Count} shown · {done} of {_monthItems.Count} uploaded";
        PhotosEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PhotosEmptyText.Text = _monthItems.Count == 0 ? "No photos here" : "Nothing matches this filter";
    }

    void PhotosRefresh_Click(object s, RoutedEventArgs e) => _ = LoadPhotosAsync();
    void PhotosOpenFolder_Click(object s, RoutedEventArgs e)
    {
        var folder = (MonthList.SelectedItem as PhotoMonth)?.Files.FirstOrDefault() is { } f ? Path.GetDirectoryName(f)! : _settings.ScreenshotFolder;
        if (Directory.Exists(folder)) OpenFolder(folder);
    }

    void Photo_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).Tag is PhotoItem p) ShowPhotoDetail(p, standalone: false);
    }

    // ---- photo detail ----

    async void ShowPhotoDetail(PhotoItem p, bool standalone)
    {
        _detail = p;
        _detailStandalone = standalone;
        PhotoDetail.Visibility = Visibility.Visible;
        PdImage.Source = p.Thumbnail; // quick preview while the full image loads
        PdWorld.Text = "Reading photo…";
        PdTaken.Text = "Taken " + p.TakenAt.ToString("dddd d MMMM yyyy, HH:mm:ss");
        PdFile.Text = p.FileName;
        PdInfo.Text = "";
        PdPlayers.Text = "";
        PdStatus.Text = "";
        _detailOnServer = new HashSet<string>();
        RenderDetailDestinations();

        var path = p.Path;
        var (img, info, size, hash) = await Task.Run(() =>
        {
            var full = PhotoItem.Load(path, 1600);
            ScreenshotInfo meta;
            long len = 0;
            string sha = "";
            try
            {
                var bytes = File.ReadAllBytes(path);
                len = bytes.Length;
                sha = DiscordUploader.Sha256Hex(bytes);
                meta = ScreenshotMetadata.Read(new MemoryStream(bytes), path);
            }
            catch { meta = new ScreenshotInfo(); }
            return (full, meta, len, sha);
        });
        if (_detail != p) return; // user moved on
        _ = CheckDetailOnServersAsync(p, hash);

        if (img != null) PdImage.Source = img;
        PdWorld.Text = info.HasWorld
            ? (string.IsNullOrEmpty(info.WorldName) ? "Unknown world name" : info.WorldName)
            : "No world info in this photo";

        var lines = new List<string>();
        if (!string.IsNullOrEmpty(info.WorldId)) lines.Add("World ID: " + info.WorldId);
        if (!string.IsNullOrEmpty(info.InstanceId)) lines.Add("Instance: " + info.InstanceId);
        var res = ScreenshotMetadata.ResolutionFromFileName(path) ?? (img is BitmapSource b ? $"{b.PixelWidth}×{b.PixelHeight}" : null);
        if (res != null) lines.Add("Size: " + res + (size > 0 ? $" · {size / 1024.0 / 1024.0:0.0} MB" : ""));
        lines.Add("Info from: " + (info.Source == "none" ? "nothing (no VRCX metadata)" : info.Source));
        PdInfo.Text = string.Join("\n", lines);

        bool hasPlayers = info.Players.Count > 0;
        PdPlayersLabel.Visibility = PdPlayers.Visibility = hasPlayers ? Visibility.Visible : Visibility.Collapsed;
        PdPlayers.Text = string.Join("  ·  ", info.Players.Select(x => x.DisplayName));
        if (hasPlayers) PdPlayersLabel.Text = $"Players ({info.Players.Count})";
    }

    /// <summary>Asks each Nekumi server whether it already has this exact photo (same bytes), then locks those.</summary>
    async Task CheckDetailOnServersAsync(PhotoItem p, string hash)
    {
        if (hash.Length == 0) return;
        var dests = _settings.Destinations.Where(d => d.AuthKey.Trim().Length > 0).ToList();
        var checks = await Task.WhenAll(dests.Select(async d => (d.Id, Posted: await DiscordUploader.CheckPostedAsync(d, new[] { hash }))));
        if (_detail != p) return;
        _detailOnServer = checks.Where(c => c.Posted?.Contains(hash) == true).Select(c => c.Id).ToHashSet();
        if (_detailOnServer.Count > 0) RenderDetailDestinations();
    }

    /// <summary>
    /// One toggle per destination with a key. Locked when the photo is already there (history or server check)
    /// or the server's new rules haven't been accepted. Pre-ticks enabled ones the photo hasn't reached yet.
    /// </summary>
    void RenderDetailDestinations()
    {
        if (_detail == null) return;
        PdDestinations.Children.Clear();
        var history = UploadHistory.Instance.Find(_detail.BaseName);
        var uploadedTo = history?.UploadedTo() ?? new HashSet<string>();
        var usable = _settings.Destinations.Where(d => d.AuthKey.Trim().Length > 0 && d.Url.Trim().Length > 0).ToList();

        foreach (var d in usable)
        {
            var r = history?.Results.FirstOrDefault(x => x.DestinationId == d.Id);
            bool already = uploadedTo.Contains(d.Id) || _detailOnServer.Contains(d.Id);
            string state = already ? "  ✓ already posted"
                : d.NeedsRulesAcceptance ? "  – accept its rules in Settings"
                : r is { Success: false } ? "  ✗ failed"
                : d.Enabled ? "" : "  (paused)";
            var cb = new CheckBox
            {
                Style = (Style)FindResource("Toggle"),
                Content = d.Name + state,
                Tag = d.Id,
                IsEnabled = !already && !d.NeedsRulesAcceptance,
                IsChecked = !already && !d.NeedsRulesAcceptance && d.Enabled,
                ToolTip = already ? "This photo is already on this server – it won't be posted twice."
                    : r is { Success: false } ? r.Error : null,
            };
            cb.Click += (_, _) => RenderDetailRules();
            PdDestinations.Children.Add(cb);
        }
        if (usable.Count == 0)
            PdDestinations.Children.Add(new TextBlock { Text = "Add a server in Settings → Upload destinations first.", Style = (Style)FindResource("Muted") });

        bool any = PdDestinations.Children.OfType<CheckBox>().Any(c => c.IsEnabled);
        PdUploadButton.IsEnabled = any;
        if (!any && usable.Count > 0 && PdDestinations.Children.OfType<CheckBox>().All(c => ((string)c.Content).Contains("already posted")))
            PdStatus.Text = "✓ Already on every server.";
        else if (history != null && PdStatus.Text.Length == 0)
            PdStatus.Text = $"Last upload {history.Time:dd MMM yyyy HH:mm}";
        RenderDetailRules();
    }

    /// <summary>The rules of every ticked server, right above the Upload button.</summary>
    void RenderDetailRules()
    {
        PdRules.Children.Clear();
        var ids = PdDestinations.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToHashSet();
        foreach (var d in _settings.Destinations.Where(x => ids.Contains(x.Id) && x.Rules.Count > 0))
        {
            var box = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xF5, 0x9E, 0x0B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xF5, 0x9E, 0x0B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 6, 0, 0),
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"⚠️ Rules – {d.Name}", FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xFC, 0xD3, 0x4D)) });
            foreach (var rule in d.Rules)
                panel.Children.Add(new TextBlock { Text = "• " + rule, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), Foreground = (Brush)FindResource("Text") });
            box.Child = panel;
            PdRules.Children.Add(box);
        }
    }

    async void PdUpload_Click(object s, RoutedEventArgs e)
    {
        if (_detail == null) return;
        var ids = PdDestinations.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        if (ids.Count == 0) { Toast("Tick at least one server", error: true); return; }
        if (!File.Exists(_detail.Path)) { Toast("File no longer exists", error: true); return; }

        var p = _detail;
        PdUploadButton.IsEnabled = false;
        PdUploadText.Text = "Uploading…";
        try
        {
            _watcher.UpdateSettings(_settings);
            var r = await _watcher.UploadManualAsync(p.Path, ids);
            if (r == null) PdStatus.Text = "Upload didn't run – see Logs.";
            else
            {
                var mine = r.Results.Where(x => ids.Contains(x.DestinationId)).ToList();
                PdStatus.Text = mine.All(x => x.Success)
                    ? $"✓ Uploaded to {string.Join(", ", mine.Select(x => x.Name))}"
                    : "✗ " + string.Join("; ", mine.Where(x => !x.Success).Select(x => $"{x.Name}: {x.Error}"));
                Toast(mine.All(x => x.Success) ? "Uploaded!" : "Some uploads failed", error: !mine.All(x => x.Success));
            }
            p.RefreshStatus();
            if (_detail == p) RenderDetailDestinations();
        }
        finally
        {
            PdUploadButton.IsEnabled = true;
            PdUploadText.Text = "Upload to selected";
        }
    }

    void StepDetail(int delta)
    {
        if (_detail == null || _detailStandalone) return;
        int i = _visiblePhotos.IndexOf(_detail);
        if (i < 0) return;
        int n = i + delta;
        if (n >= 0 && n < _visiblePhotos.Count) ShowPhotoDetail(_visiblePhotos[n], standalone: false);
    }

    void PdPrev_Click(object s, RoutedEventArgs e) => StepDetail(-1);
    void PdNext_Click(object s, RoutedEventArgs e) => StepDetail(+1);
    void PdClose_Click(object s, RoutedEventArgs e) { PhotoDetail.Visibility = Visibility.Collapsed; _detail = null; ApplyPhotoFilter(); }

    void PdShow_Click(object s, RoutedEventArgs e)
    {
        if (_detail != null && File.Exists(_detail.Path)) Process.Start("explorer.exe", $"/select,\"{_detail.Path}\"");
    }

    void PdOpen_Click(object s, RoutedEventArgs e)
    {
        if (_detail != null && File.Exists(_detail.Path)) OpenUrl(_detail.Path);
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (PhotoDetail.Visibility == Visibility.Visible && !(e.OriginalSource is TextBox))
        {
            switch (e.Key)
            {
                case System.Windows.Input.Key.Left: StepDetail(-1); e.Handled = true; return;
                case System.Windows.Input.Key.Right: StepDetail(+1); e.Handled = true; return;
                case System.Windows.Input.Key.Escape: PdClose_Click(this, new RoutedEventArgs()); e.Handled = true; return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    async void Retry_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).Tag is not HistoryItem item) return;
        if (!File.Exists(item.Entry.FilePath)) { Toast("File no longer exists", error: true); return; }

        // Partly failed: only resend to the servers that failed (and still exist + are enabled).
        IReadOnlyCollection<string>? only = null;
        var failed = item.Entry.Results.Where(r => !r.Success).Select(r => r.DestinationId).ToList();
        if (item.Entry.Results.Any(r => r.Success) && failed.Count > 0)
            only = failed.Where(id => _settings.ActiveDestinations.Any(d => d.Id == id)).ToList();
        if (only is { Count: 0 }) { Toast("The failed destinations are removed or disabled", error: true); return; }

        await UploadManual(item.Entry.FilePath, only);
    }

    async Task UploadManual(string path, IReadOnlyCollection<string>? only = null)
    {
        _watcher.UpdateSettings(_settings);
        Toast("Uploading " + Path.GetFileName(path) + "…");
        var r = await _watcher.UploadManualAsync(path, only);
        if (r != null) Toast(r.Success ? "Uploaded!" : "Failed: " + r.Error, error: !r.Success);
    }

    void ShowInFolder_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).Tag is not HistoryItem item) return;
        if (File.Exists(item.Entry.FilePath))
            Process.Start("explorer.exe", $"/select,\"{item.Entry.FilePath}\"");
        else
            Toast("File no longer exists", error: true);
    }

    // ================= Settings =================

    bool IsConfigured(out string reason, bool needFolder = true)
    {
        if (!_settings.ActiveDestinations.Any()) { reason = "Add an upload destination with a key in Settings"; return false; }
        if (needFolder && !Directory.Exists(_settings.ScreenshotFolder)) { reason = "Pick your VRChat screenshot folder in Settings"; return false; }
        reason = "";
        return true;
    }

    void UpdateSetupBanner()
    {
        bool ok = IsConfigured(out var why);
        SetupBanner.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        if (!ok) SetupBannerText.Text = "Finish setup: " + why.Replace(" in Settings", "") + ".";
    }

    void LoadSettingsIntoUi()
    {
        RefreshDestinations();
        FolderBox.Text = _settings.ScreenshotFolder;
        SubfoldersCheck.IsChecked = _settings.IncludeSubfolders;
        MetadataCheck.IsChecked = _settings.UploadWithMetadata;
        PlayersCheck.IsChecked = _settings.IncludePlayers;
        SettleSlider.Value = _settings.SettleDelayMs;
        AutoStartCheck.IsChecked = _settings.StartWatchingOnLaunch;
        WindowsStartCheck.IsChecked = _settings.StartWithWindows;
        TrayCheck.IsChecked = _settings.MinimizeToTray;
        NotifyCheck.IsChecked = _settings.ShowNotifications;
        UpdateCheck.IsChecked = _settings.CheckForUpdates;
        BetaCheck.IsChecked = _settings.BetaUpdates;
        CurrentVersionText.Text = $"You have v{AppVersion.Current}. Updates come from GitHub releases; your settings are kept.";
        SaveStatus.Text = "";
    }

    void Save_Click(object s, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        if (folder.Length > 0 && !Directory.Exists(folder))
        {
            SaveStatus.Foreground = (Brush)FindResource("Danger");
            SaveStatus.Text = "That screenshot folder doesn't exist.";
            return;
        }

        var n = _settings.Clone();
        n.ScreenshotFolder = folder;
        n.IncludeSubfolders = SubfoldersCheck.IsChecked == true;
        n.UploadWithMetadata = MetadataCheck.IsChecked == true;
        n.IncludePlayers = PlayersCheck.IsChecked == true;
        n.SettleDelayMs = (int)SettleSlider.Value;
        n.StartWatchingOnLaunch = AutoStartCheck.IsChecked == true;
        n.StartWithWindows = WindowsStartCheck.IsChecked == true;
        n.MinimizeToTray = TrayCheck.IsChecked == true;
        n.ShowNotifications = NotifyCheck.IsChecked == true;
        n.CheckForUpdates = UpdateCheck.IsChecked == true;
        n.BetaUpdates = BetaCheck.IsChecked == true;

        ApplySettings(n);
        SaveStatus.Foreground = (Brush)FindResource("Success");
        SaveStatus.Text = "✓ Saved";
        Toast("Settings saved");
    }

    void ApplySettings(AppSettings n)
    {
        bool folderChanged = n.ScreenshotFolder != _settings.ScreenshotFolder || n.IncludeSubfolders != _settings.IncludeSubfolders;
        if (n.StartWithWindows != _settings.StartWithWindows) StartupRegistration.Apply(n.StartWithWindows);

        _settings = n;
        SettingsStore.Save(_settings);
        Log.Info("Settings saved.");

        if (_watcher.IsRunning)
        {
            if (folderChanged) _watcher.Start(_settings);
            else _watcher.UpdateSettings(_settings);
        }
        UpdateSetupBanner();
        RefreshDataPage();
    }

    void Discard_Click(object s, RoutedEventArgs e) { LoadSettingsIntoUi(); Toast("Changes discarded"); }

    void Browse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select your VRChat screenshot folder",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : AppPaths.DefaultScreenshotDir,
        };
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    // ================= Upload destinations =================

    UploadDestination? _editing;   // copy being edited
    bool _editingIsNew;

    void RefreshDestinations()
    {
        DestList.ItemsSource = null;
        DestList.ItemsSource = _settings.Destinations;
        DestEmpty.Visibility = _settings.Destinations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddCommunityButton.Visibility = _settings.Destinations.Any(d => d.Kind == DestinationKind.Community)
            ? Visibility.Collapsed : Visibility.Visible;
        UpdateRulesBanner();
    }

    /// <summary>Re-reads every server's name, visibility and rules. New rules pause auto-upload there until accepted.</summary>
    async Task RefreshDestinationInfoAsync()
    {
        var updates = new List<(string Id, DestinationInfo Info)>();
        foreach (var d in _settings.Destinations.Where(x => x.AuthKey.Trim().Length > 0).ToList())
        {
            try
            {
                var info = await DiscordUploader.GetInfoAsync(d);
                if (info != null) updates.Add((d.Id, info));
            }
            catch (Exception ex) { Log.Warn($"Couldn't refresh {d.Name}: {ex.Message}"); }
        }
        bool changed = updates.Any(u =>
        {
            var d = _settings.Destinations.FirstOrDefault(x => x.Id == u.Id);
            return d != null && (!d.Rules.SequenceEqual(u.Info.Rules) || d.ServerName != u.Info.ServerName
                                 || d.ChannelName != u.Info.ChannelName || d.Visibility != u.Info.Visibility);
        });
        if (!changed) return;

        SaveDestinations(n =>
        {
            foreach (var (id, info) in updates)
            {
                var d = n.Destinations.FirstOrDefault(x => x.Id == id);
                if (d == null) continue;
                if (d.Rules.Count > 0 && !d.Rules.SequenceEqual(info.Rules))
                    Log.Warn($"{d.Name} changed its upload rules – review them in Settings before photos are posted there again.");
                d.Rules = info.Rules.ToList();
                d.ServerName = info.ServerName;
                d.ChannelName = info.ChannelName;
                d.Visibility = info.Visibility;
            }
        });
    }

    void UpdateRulesBanner()
    {
        var pending = _settings.Destinations.Where(d => d.NeedsRulesAcceptance).Select(d => d.Name).ToList();
        RulesBanner.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (pending.Count > 0)
            RulesBannerText.Text = $"{string.Join(", ", pending)} {(pending.Count == 1 ? "has" : "have")} upload rules to accept. Photos won't be posted there until you do.";
    }

    void RulesReview_Click(object s, RoutedEventArgs e)
    {
        var d = _settings.Destinations.FirstOrDefault(x => x.NeedsRulesAcceptance);
        NavSettings.IsChecked = true;
        if (d != null) OpenEditor(d.Clone(), isNew: false);
    }

    void SaveDestinations(Action<AppSettings> change)
    {
        var n = _settings.Clone();
        change(n);
        ApplySettings(n);
        RefreshDestinations();
    }

    void AddCommunity_Click(object s, RoutedEventArgs e) => OpenEditor(UploadDestination.NekoSuneCommunity(), isNew: true);

    void AddOwn_Click(object s, RoutedEventArgs e)
    {
        int n = _settings.Destinations.Count(d => d.Kind == DestinationKind.OwnServer) + 1;
        OpenEditor(new UploadDestination { Name = n == 1 ? "My server" : $"My server {n}", Kind = DestinationKind.OwnServer }, isNew: true);
    }

    void DestEdit_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).Tag is UploadDestination d) OpenEditor(d.Clone(), isNew: false);
    }

    void DestToggle_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).Tag is not UploadDestination d) return;
        bool on = ((CheckBox)s).IsChecked == true;
        SaveDestinations(n => n.Destinations.First(x => x.Id == d.Id).Enabled = on);
        Toast(on ? $"Posting to {d.Name}" : $"Paused {d.Name}");
    }

    void DestDelete_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).Tag is not UploadDestination d) return;
        if (MessageBox.Show(this, $"Remove \"{d.Name}\"? Its key is deleted from this PC (it keeps working on the server until you regenerate it).",
                "Remove destination", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SaveDestinations(n => n.Destinations.RemoveAll(x => x.Id == d.Id));
        Toast("Destination removed");
    }

    void OpenDashboard_Click(object s, RoutedEventArgs e) => OpenUrl(UploadDestination.NekumiDashboardUrl);

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void OpenEditor(UploadDestination d, bool isNew)
    {
        _editing = d;
        _editingIsNew = isNew;
        EdTitle.Text = isNew
            ? d.Kind == DestinationKind.Community ? "Add NekoSune Community" : "Add your server"
            : "Edit " + d.Name;
        EdHelp.Text = d.Kind switch
        {
            DestinationKind.Community =>
                "1. Join the NekoSune Community Discord.\n2. Run /vrchatupload key there.\n3. Paste your personal key (starts with nkvrm_) below.\nNo NSFW – abusing uploads gets your key revoked.",
            DestinationKind.OwnServer =>
                "1. Invite Nekumi to your server.\n2. Open the Nekumi dashboard → your server → Integrations → VRChat.\n3. Create an integration for the channel you want and paste its key (starts with nkvrc_).\nEach key always posts to the channel it was made for.",
            _ => "Any compatible upload URL. The key is sent as \"Authorization: Bearer <key>\".",
        };
        EdName.Text = d.Name;
        EdKey.Password = d.AuthKey;
        EdKeyPlain.Text = d.AuthKey;
        EdUrl.Text = d.Url;
        EdServer.Text = d.ServerId;
        EdChannel.Text = d.ChannelId;
        EdEnabled.IsChecked = d.Enabled;
        EdLegacyPanel.Visibility = d.Kind == DestinationKind.Custom || d.Url.Contains("{serverId}", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
        EdStatus.Text = "";
        UpdateKeyInfo();
        _edInfoFor = d.ServerName.Length > 0 || d.Rules.Count > 0 ? InfoKey(d) : null;
        RenderEditorInfo(d);
        EdAccept.IsChecked = d.Rules.Count > 0 && !d.NeedsRulesAcceptance;
        DestEditor.Visibility = Visibility.Visible;
        EdName.Focus();
    }

    string EditorKey => (EdKeyPlain.Visibility == Visibility.Visible ? EdKeyPlain.Text : EdKey.Password).Trim();

    void EdKey_Changed(object s, RoutedEventArgs e) => UpdateKeyInfo();

    void UpdateKeyInfo()
    {
        if (_editing == null || EdKeyInfo == null) return;
        var key = EditorKey;
        var kind = UploadDestination.DescribeKey(key);
        string warn = "";
        if (_editing.Kind == DestinationKind.Community && key.StartsWith("nkvrc_")) warn = " – this looks like a server key, community needs your /vrchatupload key (nkvrm_)";
        if (_editing.Kind == DestinationKind.OwnServer && key.StartsWith("nkvrm_")) warn = " – this is a community member key, not a server integration key (nkvrc_)";
        EdKeyInfo.Text = key.Length == 0 ? "Paste the key here. It is encrypted before it is saved." : kind + warn;
        EdKeyInfo.Foreground = (Brush)FindResource(warn.Length > 0 ? "Warning" : "TextMuted");
    }

    void EdReveal_Click(object s, RoutedEventArgs e)
    {
        bool showing = EdKeyPlain.Visibility == Visibility.Visible;
        if (showing) { EdKey.Password = EdKeyPlain.Text; EdKeyPlain.Visibility = Visibility.Collapsed; EdKey.Visibility = Visibility.Visible; EdRevealIcon.Text = "\uE890"; }
        else { EdKeyPlain.Text = EdKey.Password; EdKeyPlain.Visibility = Visibility.Visible; EdKey.Visibility = Visibility.Collapsed; EdRevealIcon.Text = "\uED1A"; }
    }

    void EdUrlReset_Click(object s, RoutedEventArgs e) => EdUrl.Text = UploadDestination.NekumiUploadUrl;

    UploadDestination? ReadEditor(out string error)
    {
        error = "";
        if (_editing == null) return null;
        var d = _editing.Clone();
        d.Name = EdName.Text.Trim();
        d.AuthKey = EditorKey;
        d.Url = EdUrl.Text.Trim();
        d.ServerId = EdServer.Text.Trim();
        d.ChannelId = EdChannel.Text.Trim();
        d.Enabled = EdEnabled.IsChecked == true;
        if (d.Name.Length == 0) error = "Give it a name.";
        else if (!Uri.TryCreate(d.ResolveUrl(), UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) error = "The upload URL isn't valid.";
        else if (d.AuthKey.Length == 0) error = "Paste the key.";
        else if (_settings.Destinations.Any(x => x.Id != d.Id && x.AuthKey.Trim() == d.AuthKey)) error = "Another destination already uses this key.";
        return d;
    }

    // ---- server info + rules in the editor ----

    string? _edInfoFor; // key|url the shown info belongs to

    static string InfoKey(UploadDestination d) => d.AuthKey.Trim() + "|" + d.ResolveUrl();

    void RenderEditorInfo(UploadDestination d)
    {
        var where = d.ServerName.Length > 0 ? $"Posts to {d.ServerName}" + (d.ChannelName.Length > 0 ? $" › #{d.ChannelName}" : "") : "";
        var vis = d.Visibility switch
        {
            "public" => "Public – members use their own keys",
            "private" => "Private – server key only",
            _ => "",
        };
        EdServerInfo.Text = string.Join("  ·  ", new[] { where, vis }.Where(x => x.Length > 0));
        EdRulesList.Children.Clear();
        foreach (var rule in d.Rules)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = "•", Foreground = (Brush)FindResource("Warning") });
            var t = new TextBlock { Text = rule, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Text"), FontSize = 12.5 };
            Grid.SetColumn(t, 1);
            row.Children.Add(t);
            EdRulesList.Children.Add(row);
        }
        EdRulesPanel.Visibility = d.Rules.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Asks the server where this key posts and what its rules are. Copies the result onto both
    /// <paramref name="d"/> and the copy being edited. Returns an error only for a rejected key.
    /// </summary>
    async Task<string?> FetchEditorInfoAsync(UploadDestination d)
    {
        EdStatus.Foreground = (Brush)FindResource("TextMuted");
        EdStatus.Text = "Checking the server…";
        try
        {
            var info = await DiscordUploader.GetInfoAsync(d);
            _edInfoFor = InfoKey(d);
            if (info == null)
            {
                EdStatus.Text = "This server doesn't publish rules or server info (older bot or custom URL).";
                return null;
            }
            bool rulesChanged = !info.Rules.SequenceEqual(d.Rules);
            foreach (var target in new[] { d, _editing! })
            {
                target.Rules = info.Rules.ToList();
                target.ServerName = info.ServerName;
                target.ChannelName = info.ChannelName;
                target.Visibility = info.Visibility;
            }
            RenderEditorInfo(d);
            if (rulesChanged) EdAccept.IsChecked = false;
            EdStatus.Foreground = (Brush)FindResource("Success");
            EdStatus.Text = "✓ Key works" + (info.Rules.Count > 0 ? " – read the rules below." : ".");
            return null;
        }
        catch (HttpRequestException ex) when (ex.Message.StartsWith("401") || ex.Message.StartsWith("403"))
        {
            return "The server rejected this key: " + ex.Message;
        }
        catch (Exception ex)
        {
            // offline etc.: allow saving, rules get fetched later
            EdStatus.Foreground = (Brush)FindResource("Warning");
            EdStatus.Text = "Couldn't reach the server (" + ex.Message + "). Rules will be checked later.";
            return null;
        }
    }

    async void DestSave_Click(object s, RoutedEventArgs e)
    {
        var d = ReadEditor(out var error);
        if (d == null) return;
        void Fail(string msg)
        {
            EdStatus.Foreground = (Brush)FindResource("Danger");
            EdStatus.Text = msg;
        }
        if (error.Length > 0) { Fail(error); return; }

        if (_edInfoFor != InfoKey(d))
        {
            var bad = await FetchEditorInfoAsync(d);
            if (bad != null) { Fail(bad); return; }
            d = ReadEditor(out _) ?? d; // pick up the fetched rules
        }
        if (d.Rules.Count > 0)
        {
            if (EdAccept.IsChecked != true) { Fail("Read the rules and tick “I've read these rules” first."); return; }
            d.AcceptedRulesHash = d.RulesHash;
        }

        SaveDestinations(n =>
        {
            int i = n.Destinations.FindIndex(x => x.Id == d.Id);
            if (i >= 0) n.Destinations[i] = d; else n.Destinations.Add(d);
        });
        DestEditor.Visibility = Visibility.Collapsed;
        _editing = null;
        Toast(_editingIsNew ? $"{d.Name} added" : $"{d.Name} saved");
    }

    void DestCancel_Click(object s, RoutedEventArgs e)
    {
        DestEditor.Visibility = Visibility.Collapsed;
        _editing = null;
    }

    async void DestCheck_Click(object s, RoutedEventArgs e)
    {
        var d = ReadEditor(out _);
        if (d == null) return;
        EdStatus.Foreground = (Brush)FindResource("TextMuted");
        EdStatus.Text = "Checking…";
        var r = await DiscordUploader.CheckAsync(d);
        if (!r.Success)
        {
            EdStatus.Foreground = (Brush)FindResource("Danger");
            EdStatus.Text = "✗ " + r.Message;
            return;
        }
        if (d.AuthKey.Length == 0)
        {
            EdStatus.Foreground = (Brush)FindResource("Success");
            EdStatus.Text = "✓ " + r.Message + " Paste the key to check it too.";
            return;
        }
        var bad = await FetchEditorInfoAsync(d);
        if (bad != null)
        {
            EdStatus.Foreground = (Brush)FindResource("Danger");
            EdStatus.Text = "✗ " + bad;
        }
    }

    void Settle_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SettleText != null) SettleText.Text = (e.NewValue / 1000).ToString("0.0") + " s";
    }

    // ================= Updates =================

    readonly DispatcherTimer _updateTimer = new();
    ReleaseInfo? _update;
    bool _checking, _installing;

    async Task CheckUpdatesAsync(bool manual)
    {
        if (_checking || _installing) return;
        if (!manual && !_settings.CheckForUpdates) return;
        _checking = true;
        if (manual) UpdateStatus.Text = "Checking…";
        try
        {
            var r = await UpdateChecker.CheckAsync(_settings.BetaUpdates);
            _update = r;
            if (r == null)
            {
                UpdateBanner.Visibility = Visibility.Collapsed;
                VersionText.Text = "v" + AppVersion.Current;
                if (manual) UpdateStatus.Text = "✓ You're on the latest version.";
                return;
            }

            Log.Info($"Update available: v{r.Version} ({r.Name})");
            VersionText.Text = $"v{AppVersion.Current} · update available";
            UpdateTitle.Text = $"Version {r.Version} is available";
            UpdateSub.Text = $"You have v{AppVersion.Current}" + (r.Published > DateTime.MinValue ? $" · released {r.Published.ToLocalTime():dd MMM yyyy}" : "");
            UpdateNowButton.Content = r.ZipUrl != null && Updater.CanSelfUpdate() ? "Update now" : "Download";
            bool skipped = !manual && _settings.SkippedVersion == r.Version.ToString();
            UpdateBanner.Visibility = skipped ? Visibility.Collapsed : Visibility.Visible;
            if (manual)
            {
                UpdateStatus.Text = $"v{r.Version} is available – see the Dashboard.";
                NavDashboard.IsChecked = true;
            }
            else if (!skipped && !IsVisible && _tray != null)
            {
                _tray.ShowBalloonTip(5000, "Update available", $"Version {r.Version} is ready to install.", Forms.ToolTipIcon.None);
            }
        }
        catch (Exception ex)
        {
            if (manual) UpdateStatus.Text = "Couldn't check: " + ex.Message;
            Log.Warn("Update check failed: " + ex.Message);
        }
        finally { _checking = false; }
    }

    async Task FirstUpdateCheckAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        await CheckUpdatesAsync(manual: false);
        await RefreshDestinationInfoAsync();
    }

    async void CheckUpdates_Click(object s, RoutedEventArgs e)
    {
        // use the toggles as shown, even if not saved yet
        var beta = _settings.BetaUpdates;
        _settings.BetaUpdates = BetaCheck.IsChecked == true;
        try { await CheckUpdatesAsync(manual: true); }
        finally { _settings.BetaUpdates = beta; }
    }

    void OpenReleases_Click(object s, RoutedEventArgs e) => OpenUrl(UpdateChecker.ReleasesPage);

    // ---- What's new ----

    string _whatsNewUrl = UpdateChecker.ReleasesPage;

    void ShowWhatsNew(string title, string sub, string markdown, string url, bool offerUpdate)
    {
        WnTitle.Text = title;
        WnSub.Text = sub;
        _whatsNewUrl = string.IsNullOrEmpty(url) ? UpdateChecker.ReleasesPage : url;
        MarkdownView.Render(WnNotes, markdown);
        WnUpdate.Visibility = offerUpdate ? Visibility.Visible : Visibility.Collapsed;
        WnUpdate.Content = UpdateNowButton.Content;
        WnClose.Content = offerUpdate ? "Later" : "Close";
        WhatsNew.Visibility = Visibility.Visible; // shown next time the window is opened if it's in the tray
    }

    /// <summary>Upcoming update: changelog of every version between the running one and the update.</summary>
    void UpdateNotes_Click(object s, RoutedEventArgs e)
    {
        if (_update == null) return;
        ShowWhatsNew($"Version {_update.Version} is available",
            $"You have v{AppVersion.Current}. Changes since your version:",
            _update.Notes, _update.PageUrl, offerUpdate: true);
    }

    /// <summary>First start after an update: notes for the new version from the CHANGELOG.md built into the exe.</summary>
    void ShowWhatsNewAfterUpdate()
    {
        var now = AppVersion.Current.ToString();
        var last = _settings.LastRunVersion;
        if (last == now) return;
        SaveDestinations(n => n.LastRunVersion = now);
        if (string.IsNullOrEmpty(last)) return; // fresh install

        var notes = Changelog.For(AppVersion.Current, Changelog.Embedded());
        if (string.IsNullOrWhiteSpace(notes)) return;
        Log.Success($"Updated from v{last} to v{now}");
        ShowWhatsNew($"Updated to v{now}", $"You were on v{last}. Here's what changed:", notes, UpdateChecker.ReleasesPage, offerUpdate: false);
    }

    void ShowChangelog_Click(object s, RoutedEventArgs e)
    {
        var md = Changelog.Embedded();
        ShowWhatsNew("Changelog", $"You have v{AppVersion.Current}", md.Length > 0 ? md : "Changelog not available in this build.",
            UpdateChecker.ReleasesPage, offerUpdate: false);
    }

    void WnClose_Click(object s, RoutedEventArgs e) => WhatsNew.Visibility = Visibility.Collapsed;
    void WnGitHub_Click(object s, RoutedEventArgs e) => OpenUrl(_whatsNewUrl);

    void UpdateSkip_Click(object s, RoutedEventArgs e)
    {
        if (_update == null) return;
        var v = _update.Version.ToString();
        SaveDestinations(n => n.SkippedVersion = v);
        UpdateBanner.Visibility = Visibility.Collapsed;
        Toast($"Skipped v{v}");
    }

    async void UpdateNow_Click(object s, RoutedEventArgs e)
    {
        if (_update == null || _installing) return;
        WhatsNew.Visibility = Visibility.Collapsed;
        if (_update.ZipUrl == null || !Updater.CanSelfUpdate())
        {
            // e.g. installed in Program Files: let them download it by hand
            OpenUrl(string.IsNullOrEmpty(_update.PageUrl) ? UpdateChecker.ReleasesPage : _update.PageUrl);
            return;
        }
        if (MessageBox.Show(this, $"Download and install v{_update.Version}?\n\nThe app closes, updates and opens again. Your settings and history are kept.",
                "Update", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        _installing = true;
        UpdateNowButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => UpdateSub.Text = $"Downloading… {p:P0}");
            var dir = await Updater.DownloadAsync(_update, progress);
            UpdateSub.Text = "Installing…";
            _watcher.Stop();
            Updater.LaunchInstaller(dir);
            Quit();
        }
        catch (Exception ex)
        {
            Log.Error("Update failed: " + ex.Message);
            UpdateSub.Text = "Update failed: " + ex.Message;
            Toast("Update failed – try Download instead", error: true);
            UpdateNowButton.Content = "Download";
            UpdateNowButton.IsEnabled = true;
            _update = _update with { ZipUrl = null };
            _installing = false;
        }
    }

    // ================= Data =================

    void RefreshDataPage()
    {
        DataPathBox.Text = AppPaths.DataDir;
        DataSchema.Text = "v" + AppSettings.CurrentSchema;
        try
        {
            long bytes = Directory.Exists(AppPaths.LogsDir)
                ? Directory.GetFiles(AppPaths.LogsDir).Sum(f => new FileInfo(f).Length) : 0;
            DataLogSize.Text = bytes < 1024 * 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes / 1024.0 / 1024.0:0.0} MB";
        }
        catch { DataLogSize.Text = "?"; }
    }

    void OpenData_Click(object s, RoutedEventArgs e) => OpenFolder(AppPaths.DataDir);
    void OpenLogs_Click(object s, RoutedEventArgs e) => OpenFolder(AppPaths.LogsDir);

    static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    void Export_Click(object s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Backup (*.json)|*.json",
            FileName = $"vrcx-uploader-backup-{DateTime.Now:yyyy-MM-dd}.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            SettingsStore.Export(_settings, dlg.FileName);
            Log.Success("Exported backup to " + dlg.FileName);
            Toast("Backup exported");
        }
        catch (Exception ex) { Toast("Export failed: " + ex.Message, error: true); }
    }

    void Import_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Backup (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;

        var withHistory = MessageBox.Show(this, "Also replace your upload history with the one in this backup?",
            "Import backup", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (withHistory == MessageBoxResult.Cancel) return;

        try
        {
            SettingsStore.BackupFile(AppPaths.SettingsFile, "settings.before-import");
            SettingsStore.BackupFile(AppPaths.HistoryFile, "history.before-import");
            var n = SettingsStore.Import(dlg.FileName, withHistory == MessageBoxResult.Yes);
            ApplySettings(n);
            LoadSettingsIntoUi();
            Log.Success("Imported backup " + dlg.FileName);
            Toast("Backup imported");
        }
        catch (Exception ex) { Toast("Import failed: " + ex.Message, error: true); }
    }

    void ImportEnv_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = ".env file|.env;*.env|All files|*.*",
            InitialDirectory = AppPaths.ExeDir,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var n = _settings.Clone();
            SettingsStore.ImportEnv(dlg.FileName, n);
            ApplySettings(n);
            LoadSettingsIntoUi();
            Log.Success("Imported settings from " + dlg.FileName);
            Toast(".env imported");
        }
        catch (Exception ex) { Toast("Import failed: " + ex.Message, error: true); }
    }

    void ClearHistory_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Clear the upload history? Screenshots already posted to Discord stay there.",
                "Clear history", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        SettingsStore.BackupFile(AppPaths.HistoryFile, "history.before-clear");
        UploadHistory.Instance.Clear();
        Toast("History cleared");
    }

    void ResetAll_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Reset all settings and history to default? A backup copy is kept in the backups folder.",
                "Reset everything", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _watcher.Stop();
        if (_settings.StartWithWindows) StartupRegistration.Apply(false);
        SettingsStore.ResetAll();
        _settings = new AppSettings();
        SettingsStore.Save(_settings);
        LoadSettingsIntoUi();
        UpdateSetupBanner();
        RefreshDataPage();
        Log.Info("All settings reset.");
        Toast("Reset complete");
    }

    // ================= Logs =================

    void AddLog(LogEntry e)
    {
        _logs.Add(e);
        while (_logs.Count > 1000) _logs.RemoveAt(0);
        if (PageLogs.Visibility == Visibility.Visible) LogList.ScrollIntoView(e);
    }

    void ClearLog_Click(object s, RoutedEventArgs e) => _logs.Clear();

    void CopyLog_Click(object s, RoutedEventArgs e)
    {
        Clipboard.SetText(string.Join(Environment.NewLine, _logs.Select(l => $"[{l.TimeText}] {l.LevelText} {l.Message}")));
        Toast("Log copied");
    }

    // ================= Toast =================

    void Toast(string text, bool error = false)
    {
        ToastText.Text = text;
        ToastText.Foreground = (Brush)FindResource(error ? "Danger" : "Text");
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3000))));
        ToastBorder.BeginAnimation(OpacityProperty, anim);
    }
}
