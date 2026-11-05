using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ApertureOS.Services;

/// <summary>
/// Brings one of our windows back to the front when a game session ends.
///
/// A plain Window.Activate() is not enough. Windows' foreground lock only lets a process take the
/// foreground if it received the last input event, and XInput controller presses don't count as
/// input for that rule. So when a game closes, Windows hands the foreground to whatever it picks
/// next - usually the Steam client, which also raises its own window when a game exits - and our
/// Activate() silently fails. Console Mode then sits hidden behind Steam with the controller
/// dead, because the grid only reacts while this app is the foreground app.
/// </summary>
public static class WindowFocus
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const int SwRestore = 9;
    private const byte VkMenu = 0x12; // Alt
    private const uint KeyEventFExtendedKey = 0x0001;
    private const uint KeyEventFKeyUp = 0x0002;

    // Processes that commonly grab the foreground right after a game exits. During the reclaim
    // window we only take the foreground back from these - if the user deliberately switched to
    // any other app, we leave it alone.
    private static readonly HashSet<string> ReclaimFromProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", // Steam client and its UI
        "explorer",                // desktop / taskbar
        "Launcher", "RockstarService", "SocialClubHelper" // Rockstar Games Launcher (RDR2, GTA V)
    };

    // Steam doesn't always raise itself at the exact moment the game closes (cloud sync, the
    // library window restoring), so keep watching for a few seconds rather than trying once.
    private static readonly TimeSpan ReclaimWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ReclaimInterval = TimeSpan.FromMilliseconds(250);

    private static DispatcherTimer? _reclaimTimer;

    /// <summary>
    /// Forces <paramref name="window"/> to the foreground now, then keeps taking it back from
    /// Steam/the desktop/the Rockstar launcher for a few seconds if they grab it.
    /// </summary>
    public static void ReturnToFront(Window window)
    {
        ForceForeground(window);

        _reclaimTimer?.Stop();
        var deadline = DateTime.UtcNow + ReclaimWindow;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = ReclaimInterval };
        timer.Tick += (_, _) =>
        {
            if (!window.IsVisible || DateTime.UtcNow >= deadline)
            {
                timer.Stop();
                return;
            }

            if (!IsForeground(window) && ForegroundBelongsToReclaimableProcess())
                ForceForeground(window);
        };
        _reclaimTimer = timer;
        timer.Start();
    }

    public static bool IsForeground(Window window) =>
        GetForegroundWindow() == new WindowInteropHelper(window).Handle;

    public static void ForceForeground(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);

        var foregroundWindow = GetForegroundWindow();
        uint foregroundThreadId = foregroundWindow == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foregroundWindow, out _);
        uint currentThreadId = GetCurrentThreadId();
        bool attached = foregroundThreadId != 0 && foregroundThreadId != currentThreadId &&
                        AttachThreadInput(currentThreadId, foregroundThreadId, true);

        // From the LockSetForegroundWindow docs: "The system automatically enables calls to
        // SetForegroundWindow if the user presses the ALT key". Holding a synthetic Alt around
        // the call lifts the foreground lock the same way.
        keybd_event(VkMenu, 0, KeyEventFExtendedKey, UIntPtr.Zero);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            keybd_event(VkMenu, 0, KeyEventFExtendedKey | KeyEventFKeyUp, UIntPtr.Zero);
            if (attached)
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }

        // Z-order fallback: a Topmost flip lifts the window above every normal window even if
        // the foreground change itself was refused.
        window.Topmost = true;
        window.Topmost = false;
        window.Activate();
    }

    private static bool ForegroundBelongsToReclaimableProcess()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
            return true; // nothing focused (mid-transition) - safe to take

        GetWindowThreadProcessId(foregroundWindow, out uint pid);
        if (pid == 0)
            return true;
        if (pid == (uint)Environment.ProcessId)
            return false; // one of our own windows (a dialog, the overlay) - leave it

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return ReclaimFromProcesses.Contains(process.ProcessName);
        }
        catch (Exception)
        {
            return true; // exited between the two calls
        }
    }
}
