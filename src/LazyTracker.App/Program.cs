using System.Windows.Forms;
using LazyTracker.App;
using LazyTracker.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ─────────────────────────────────────────────────────────────
//  LazyTracker — Stay focused or visit McDonald's Romania careers
// ─────────────────────────────────────────────────────────────
//
//  This app uses your webcam to detect if you're facing the screen.
//  If your face isn't detected for 5+ seconds (configurable), it opens
//  https://www.mcdonalds.ro/cariere in your browser as motivation
//  to get back to studying.
//
//  Core detection engine (LazyTracker.Core) is fully cross-platform.
//  This host app uses WinForms tray icon on Windows.
//  For Linux/macOS, retarget to net8.0 and remove tray code.
// ─────────────────────────────────────────────────────────────

// Load configuration
// For single-file publish, check both the exe directory and AppContext.BaseDirectory
var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

var configuration = new ConfigurationBuilder()
    .SetBasePath(exeDir)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false)
    .Build();

var options = new LazyTrackerOptions();
configuration.GetSection("LazyTracker").Bind(options);

// Build the host
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole();

// Register services
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<FaceWatcher>();

if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<TrayIconManager>();
}

builder.Services.AddHostedService<FocusMonitorService>();

var host = builder.Build();

// On Windows: run with a WinForms message loop for the tray icon
if (OperatingSystem.IsWindows())
{
    var trayManager = host.Services.GetRequiredService<TrayIconManager>();
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

    // Start the host (and background service) asynchronously
    _ = host.StartAsync();

    // Initialize the tray icon on the UI thread
    trayManager.Initialize();

    // When the host stops, exit the WinForms message loop
    lifetime.ApplicationStopping.Register(() =>
    {
        trayManager.Dispose();
        Application.ExitThread();
    });

    // Run the WinForms message loop (required for NotifyIcon)
    Application.Run();

    // After message loop exits, stop the host gracefully
    host.StopAsync().GetAwaiter().GetResult();
}
else
{
    // Non-Windows: just run the host normally (console mode)
    host.Run();
}
