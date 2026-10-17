using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using SysTune.App.Services;
using SysTune.Core.Logging;
using SysTune.Core.Settings;

namespace SysTune.App;

public partial class App : Application
{
    private FileLog? _log;
    private SingleInstance? _instance;
    private SettingsStore? _store;
    private AppController? _controller;
    private static readonly JsonSerializerOptions SmokeJson = new() { WriteIndented = true };
    private int _dispatcherErrors;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _log = new FileLog(AppPaths.LogFile, e.Args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Info);
        _log.Info($"SysTune starting (v{typeof(App).Assembly.GetName().Version}, Windows build {Environment.OSVersion.Version.Build}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _log.Error("Fatal unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Warn("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            _log.Info("Another instance is running; activating it");
            _instance.SignalFirstInstance();
            Shutdown();
            return;
        }

        _store = new SettingsStore(AppPaths.SettingsFile, _log);
        _store.Load();

        _controller = new AppController(_log, _store);
        _controller.Start(autostart: e.Args.Contains("--autostart"));
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(() => _controller?.ShowWidget()));
        if (e.Args.Contains("--show-settings")) _controller.OpenSettings();

        var smoke = Array.IndexOf(e.Args, "--smoke-test");
        if (smoke >= 0)
        {
            var seconds = smoke + 1 < e.Args.Length && int.TryParse(e.Args[smoke + 1], out var s) ? s : 10;
            StartSmokeTest(seconds);
        }
    }

    /// <summary>
    /// CI/self-check mode: runs the real app for N seconds, then writes the latest real snapshot to
    /// smoke-result.json next to the log and exits with code 0 (data seen) or 2 (no data).
    /// </summary>
    private void StartSmokeTest(int seconds)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var snapshot = _controller?.LastSnapshot;
            var path = Path.Combine(AppPaths.LogDir, "smoke-result.json");
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    ok = snapshot is not null,
                    windowsBuild = Environment.OSVersion.Version.Build,
                    snapshot,
                }, SmokeJson));
            }
            catch (Exception ex)
            {
                _log?.Error("Smoke result write failed", ex);
            }
            _log?.Info($"Smoke test finished: {(snapshot is null ? "NO DATA" : "ok")}");
            _controller?.Exit(snapshot is null ? 2 : 0);
        };
        timer.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception", e.Exception);
        // Keep the widget alive for isolated failures; bail out if errors keep coming.
        if (Interlocked.Increment(ref _dispatcherErrors) < 20) e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _controller?.Dispose();
            _store?.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Error("Error during shutdown", ex);
        }
        _instance?.Dispose();
        _log?.Info("SysTune exited");
        base.OnExit(e);
    }
}
