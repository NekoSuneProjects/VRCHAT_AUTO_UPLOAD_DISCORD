using System.Threading;
using System.Windows;
using System.Windows.Threading;
using VRCX_Image_Discord_Uploader.Core;

namespace VRCX_Image_Discord_Uploader;

public partial class App : Application
{
    Mutex? _single;
    EventWaitHandle? _showSignal;
    const string InstanceName = "NekoSuneVR.VRCXImageDiscordUploader";

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            // Already running: ask the other instance to show itself, then quit.
            try { EventWaitHandle.OpenExisting(InstanceName + ".show").Set(); } catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Fatal: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Background: " + a.Exception.GetBaseException().Message); a.SetObserved(); };

        AppPaths.Ensure();
        Log.CleanupOld();
        Log.Info($"{AppPaths.AppName} v{typeof(App).Assembly.GetName().Version?.ToString(3)} starting. Data: {AppPaths.DataDir}");

        var settings = SettingsStore.Load();
        UploadHistory.Instance.Limit = settings.HistoryLimit;
        UploadHistory.Instance.Load();

        bool minimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var win = new MainWindow(settings);
        MainWindow = win;
        if (minimized && settings.MinimizeToTray) win.StartHidden();
        else win.Show();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".show");
        var t = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.Invoke(win.ShowFromTray);
        }) { IsBackground = true };
        t.Start();

        base.OnStartup(e);
    }

    void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unexpected error: " + e.Exception.Message);
        MessageBox.Show(e.Exception.Message, "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
