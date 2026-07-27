using Microsoft.Win32;

namespace ApertureOS.Services;

/// <summary>Manages the "start ApertureOS when Windows starts" preference via the per-user Run registry key
/// (no admin rights required, unlike Task Scheduler or a Startup-folder shortcut).</summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ApertureOS";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }

    /// <summary>Returns false if the registry write didn't go through (e.g. ProcessPath unavailable,
    /// or the key couldn't be opened) - the caller should tell the user, since the app's global
    /// DispatcherUnhandledException handler would otherwise swallow a thrown exception here with
    /// no visible sign that the checkbox didn't actually take effect.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    DebugLog.Write("StartupService.SetEnabled(true): Environment.ProcessPath was null/empty.");
                    return false;
                }

                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"StartupService.SetEnabled({enabled}) failed: {ex}");
            return false;
        }
    }
}
