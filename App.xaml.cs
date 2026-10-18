using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SpotifyNotch;

public partial class App : Application
{
    Mutex? _mutex;
    TrayIcon? _tray;
    NotchWindow? _window;

    static App()
    {
        // WPF renders at 60 fps unless told otherwise; on a 144 Hz screen that looks choppy next to the rest of Windows.
        // Must run before any animation/timeline type is touched.
        try
        {
            System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(System.Windows.Media.Animation.Timeline),
                new FrameworkPropertyMetadata(Native.DisplayRefreshRate()));
        }
        catch { /* already overridden: keep WPF's default */ }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => { Log.Write("unhandled: " + args.Exception); args.Handled = true; };

        _mutex = new Mutex(true, @"Local\SpotifyNotch.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        try
        {
            var settings = Settings.Load();
            NotchWindow.PerfLog = e.Args.Contains("--perf");
            if (settings.SoftwareRendering)
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            bool demo = e.Args.Contains("--demo");
            bool openAtStart = e.Args.Contains("--open");

            var media = new MediaService(demo ? "" : settings.SourceFilter);
            var spotify = new SpotifyFeatures(settings.SpotifyClientId, demo); // demo mode fakes everything and never talks to Spotify
            _window = new NotchWindow(settings, media, spotify);
            _window.EnsureHandle();
            var controller = new NotchController(settings, _window, media);
            _window.HotkeyPressed += controller.Toggle;

            _tray = new TrayIcon(settings, controller, spotify, Shutdown);
            if (!_window.RegisterHotkey(settings.Hotkey))
                _tray.Balloon($"Hotkey {settings.Hotkey} is unavailable (taken by another app or invalid). Change it in settings.json.");

            if (demo) media.StartDemo();
            else await media.StartAsync();

            _window.ScheduleTrim();
            if (!openAtStart) _window.Prewarm(); // run one hidden open/close so the first real open is already compiled and warm

            if (openAtStart)
            {
                await Task.Delay(600);
                controller.Toggle();
            }
        }
        catch (Exception ex)
        {
            Log.Write("startup failed: " + ex);
            MessageBox.Show("Spotify Notch failed to start:\n\n" + ex.Message, "Spotify Notch");
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
