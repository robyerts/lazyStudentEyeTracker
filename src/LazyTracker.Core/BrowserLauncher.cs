using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LazyTracker.Core;

/// <summary>
/// Cross-platform browser launcher. Opens a URL in the user's default browser
/// and brings the browser window to the foreground.
/// </summary>
public static class BrowserLauncher
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    /// <summary>
    /// Opens the specified URL in the default browser and brings it to focus.
    /// Works on Windows, Linux, and macOS.
    /// </summary>
    public static void OpenUrl(string url)
    {
        Process? browserProcess = null;

        try
        {
            browserProcess = Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                browserProcess = Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"")
                {
                    CreateNoWindow = true
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                browserProcess = Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                browserProcess = Process.Start("open", url);
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            BringBrowserToFront(browserProcess);
        }
    }

    private static void BringBrowserToFront(Process? launchedProcess)
    {
        try
        {
            // Give the browser a moment to open/create the tab
            Thread.Sleep(600);

            // Try the launched process first
            if (launchedProcess != null && !launchedProcess.HasExited)
            {
                var hwnd = launchedProcess.MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    FocusWindow(hwnd);
                    return;
                }
            }

            // UseShellExecute often returns a proxy process, so fall back
            // to finding common browser processes by name
            var browserNames = new[] { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };

            foreach (var name in browserNames)
            {
                try
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        if (proc.MainWindowHandle != IntPtr.Zero)
                        {
                            FocusWindow(proc.MainWindowHandle);
                            return;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static void FocusWindow(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);

        SetForegroundWindow(hwnd);
    }
}
