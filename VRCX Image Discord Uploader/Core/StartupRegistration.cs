using Microsoft.Win32;

namespace VRCX_Image_Discord_Uploader.Core;

public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "VRCX Image Discord Uploader";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{AppPaths.ExePath}\" --minimized");
            else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
        }
        catch (Exception ex) { Log.Warn("Could not update Windows startup: " + ex.Message); }
    }
}
