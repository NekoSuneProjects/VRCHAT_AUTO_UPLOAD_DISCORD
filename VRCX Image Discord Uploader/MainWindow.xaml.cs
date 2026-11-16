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
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(manual: false);
        _updateTimer.Start();
        _ = FirstUpdateCheckAsync();

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
        if (tag == "Data") RefreshDataPage();
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
        var all = UploadHistory.Instance.Snapshot();
        _recent.Clear();
        foreach (var h in all.Take(100)) _recent.Add(new HistoryItem(h));
        EmptyState.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        StatToday.Text = all.Count(h => h.Success && h.Time.Date == DateTime.Today).ToString();
        StatTotal.Text = all.Count(h => h.Success).ToString();
        StatFailed.Text = all.Count(h => !h.Success).ToString();
        DataHistoryCount.Text = all.Count.ToString();
    }

    async void UploadFile_Click(object s, RoutedEventArgs e)
    {
        if (!IsConfigured(out var why, needFolder: false)) { Toast(why, error: true); return; }
        var dlg = new OpenFileDialog
        {
            Filter = "PNG screenshots|*.png",
            InitialDirectory = Directory.Exists(_settings.ScreenshotFolder) ? _settings.ScreenshotFolder : "",
        };
        if (dlg.ShowDialog(this) != true) return;
        await UploadManual(dlg.FileName);
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

    void DestSave_Click(object s, RoutedEventArgs e)
    {
        var d = ReadEditor(out var error);
        if (d == null) return;
        if (error.Length > 0)
        {
            EdStatus.Foreground = (Brush)FindResource("Danger");
            EdStatus.Text = error;
            return;
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
        EdStatus.Foreground = (Brush)FindResource(r.Success ? "Success" : "Danger");
        EdStatus.Text = (r.Success ? "✓ " : "✗ ") + r.Message
            + (r.Success && d.AuthKey.Length > 0 ? " The key itself is checked on the first upload." : "");
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

    void UpdateNotes_Click(object s, RoutedEventArgs e) =>
        OpenUrl(string.IsNullOrEmpty(_update?.PageUrl) ? UpdateChecker.ReleasesPage : _update!.PageUrl);

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
