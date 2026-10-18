using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpotifyNotch;

public enum NotchState { Hidden, Peek, Expanded }

public partial class NotchWindow : Window
{
    public const double ExpW = 480, ExpH = 184, QueueH = 340, PeekW = 330, PeekH = 38, HiddenW = 170, Ear = 14;
    const double WinW = 528, CX = WinW / 2;
    const double ExpLeft = CX - ExpW / 2, PeekLeft = CX - PeekW / 2;
    const int HotkeyId = 0x5A17;
    const double AlbumTint = 0.25; // how far the neon hues lean toward the album colour

    static readonly Color[] Neon =
    {
        Color.FromRgb(64, 224, 255),  // electric cyan
        Color.FromRgb(123, 92, 255),  // violet
        Color.FromRgb(255, 84, 214),  // hot magenta
    };

    readonly Settings _settings;
    readonly MediaService _media;
    readonly AudioVisualizer _viz = new();
    readonly SpotifyFeatures _sp;
    // bars and progress use the album accent lightened toward white so they stay readable on the neon wave
    readonly SolidColorBrush _accent = new(ArtColor.Fallback);
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly System.Windows.Threading.DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    Spring _w, _h;
    TimeSpan _last;
    bool _rendering;
    IntPtr _hwnd;
    bool _hotkeyRegistered;

    // colours
    Color _accentCur = ArtColor.Fallback, _accentTarget = ArtColor.Fallback;
    readonly Color[] _waveCur = (Color[])Neon.Clone();
    readonly Color[] _waveTarget = (Color[])Neon.Clone();
    readonly Color[] _waveDrawn = new Color[3];

    // frame pacing: springs run every frame while moving, everything else ~30 fps once settled
    // Frames come from a plain timer, not CompositionTarget.Rendering: that event only fires while something on
    // screen is dirty, so a settled frame could leave the loop silently stalled with the window still visible.
    readonly System.Windows.Threading.DispatcherTimer _frameTimer = new(System.Windows.Threading.DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(16),
    };
    bool _fastFrames = true;
    long _lastFrameTicks, _hiddenAtTicks;
    bool _wasMoving, _geomStale = true;
    float _barsLastEnergy;

    // interaction
    bool _scrubbing;
    double _scrubFrac;
    TimeSpan _volAt = TimeSpan.FromDays(-1);
    int _shownPosSec = -1, _shownDurSec = -1;

    // track-change transitions
    BitmapSource? _shownArt;
    int _skipDir;
    long _skipAtTicks;
    int _artDir;
    double _artT = 1, _textT = 1;
    double _playMix, _playTarget;
    bool _marqueeDirty = true;
    double _titleOverflow, _marqueeStart;
    static readonly Brush MarqueeMask = MakeMarqueeMask();

    static Brush MakeMarqueeMask()
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.0));
        b.GradientStops.Add(new GradientStop(Colors.Black, 0.035));
        b.GradientStops.Add(new GradientStop(Colors.Black, 0.94));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.0));
        b.Freeze();
        return b;
    }

    public NotchState State { get; private set; } = NotchState.Hidden;
    public double Scale { get; private set; } = 1;
    public bool Interacting => _scrubbing || _qDragging;

    // the expanded notch is 184 px tall normally and grows to QueueH while the queue page is open
    bool _tall;
    public double ExpandedHeight => _tall ? QueueH : ExpH;
    public event Action? HotkeyPressed;

    public NotchWindow(Settings settings, MediaService media, SpotifyFeatures spotify)
    {
        _settings = settings;
        _media = media;
        _sp = spotify;
        InitializeComponent();
        AllowsTransparency = settings.LayeredWindow; // must be decided before the window handle exists

        _w.Snap(HiddenW);
        _h.Snap(0);
        _frameTimer.Tick += OnFrame;
        _trimTimer.Tick += (_, _) => { _trimTimer.Stop(); if (State == NotchState.Hidden) Native.TrimMemory(); };

        Bars.Levels = _viz.Levels;
        Bars.Fill = _accent;
        Bars.Count = AudioVisualizer.Bands;
        PeekBars.Levels = _viz.Levels;
        PeekBars.Fill = _accent;
        PeekBars.Count = 5;
        PeekBars.Centered = true;
        ProgFill.Background = _accent;

        PrevBtn.Click += (_, _) => { NoteSkip(-1); _ = _media.PreviousAsync(); };
        NextBtn.Click += (_, _) => { NoteSkip(+1); _ = _media.NextAsync(); };
        PlayBtn.Click += (_, _) => _ = _media.PlayPauseAsync();

        Track.MouseLeftButtonDown += (_, e) => { _scrubbing = true; Track.CaptureMouse(); UpdateScrub(e); };
        Track.MouseMove += (_, e) => { if (_scrubbing) UpdateScrub(e); };
        Track.MouseLeftButtonUp += (_, e) => EndScrub(e);
        Track.MouseEnter += (_, _) => FadeTo(Thumb, 1, 140);
        Track.MouseLeave += (_, _) => { if (!_scrubbing) FadeTo(Thumb, 0, 240); };

        PreviewMouseWheel += OnWheel;
        _media.Changed += ApplyNowPlaying;
        ApplyNowPlaying();
        WireSpotify();
        WireBackgroundSettings();
    }

    void NoteSkip(int dir)
    {
        _skipDir = dir;
        _skipAtTicks = Environment.TickCount64;
    }

    // ---- window plumbing ----------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        if (!AllowsTransparency) MakeGlassTransparent();
    }

    /// <summary>
    /// A normal (non-layered) window rendered by the GPU and made per-pixel transparent by DWM: extend the glass frame over the
    /// whole client area and render with a transparent background. Avoids WPF's UpdateLayeredWindow path.
    /// </summary>
    void MakeGlassTransparent()
    {
        var src = HwndSource.FromHwnd(_hwnd);
        if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;

        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        Native.DwmSetWindowAttribute(_hwnd, 34, ref noBorder, sizeof(int));  // DWMWA_BORDER_COLOR
        int doNotRound = 1;
        Native.DwmSetWindowAttribute(_hwnd, 33, ref doNotRound, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = DONOTROUND
    }

    /// <summary>
    /// Non-layered windows are not click-through where they are transparent, so keep the window's hit area tight around the
    /// notch: the body rectangle plus a thin strip along the top for the ears, following it as it grows and shrinks.
    /// </summary>
    void UpdateHitRegion(double w, double h)
    {
        if (_hwnd == IntPtr.Zero || AllowsTransparency) return;
        double s = Scale;
        int bodyL = Math.Max(0, (int)Math.Floor((CX - w / 2 - 1) * s));
        int bodyR = (int)Math.Ceiling((CX + w / 2 + 1) * s);
        int bodyB = Math.Max(1, (int)Math.Ceiling((h + 3) * s));
        int earL = Math.Max(0, (int)Math.Floor((CX - w / 2 - Ear - 2) * s));
        int earR = (int)Math.Ceiling((CX + w / 2 + Ear + 2) * s);
        int earB = Math.Min(bodyB, (int)Math.Ceiling((Ear + 3) * s));

        IntPtr body = Native.CreateRectRgn(bodyL, 0, bodyR, bodyB);
        IntPtr ears = Native.CreateRectRgn(earL, 0, earR, earB);
        Native.CombineRgn(body, body, ears, Native.RGN_OR);
        Native.DeleteObject(ears);
        Native.SetWindowRgn(_hwnd, body, false); // the system takes ownership of the combined region
    }

    public void EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public bool RegisterHotkey(string spec)
    {
        UnregisterHotkey();
        if (!TryParseHotkey(spec, out uint mod, out uint vk)) return false;
        _hotkeyRegistered = Native.RegisterHotKey(_hwnd, HotkeyId, mod | Native.MOD_NOREPEAT, vk);
        return _hotkeyRegistered;
    }

    public void UnregisterHotkey()
    {
        if (_hotkeyRegistered) Native.UnregisterHotKey(_hwnd, HotkeyId);
        _hotkeyRegistered = false;
    }

    static bool TryParseHotkey(string spec, out uint mod, out uint vk)
    {
        mod = 0;
        vk = 0;
        foreach (var raw in spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mod |= Native.MOD_CONTROL; break;
                case "alt": mod |= Native.MOD_ALT; break;
                case "shift": mod |= Native.MOD_SHIFT; break;
                case "win": case "windows": mod |= Native.MOD_WIN; break;
                default:
                    if (!Enum.TryParse<Key>(raw, true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0 && mod != 0;
    }

    // ---- geometry of the targets (screen DIPs, y = 0 is the screen top) -------------------

    public double ScreenWidthDip => Native.GetSystemMetrics(0) / Scale;

    public Rect TargetRect(NotchState s)
    {
        var (w, h) = s switch
        {
            NotchState.Expanded => (ExpW, ExpandedHeight),
            NotchState.Peek => (PeekW, PeekH),
            _ => (HiddenW, 0.0),
        };
        return new Rect((ScreenWidthDip - w) / 2, 0, w, h);
    }

    // ---- state ------------------------------------------------------------------------

    // ---- startup warm-up ----------------------------------------------------------------------
    // The first open after launch would otherwise pay for JIT compilation and first layout in the middle of the animation.
    // So once at startup the notch opens and closes far off-screen where nobody can see it.
    bool _prewarm, _inPrewarm;

    public async void Prewarm()
    {
        if (State != NotchState.Hidden || IsVisible) return;
        _prewarm = true;
        _inPrewarm = true;
        SetState(NotchState.Expanded);
        _inPrewarm = false;
        await Task.Delay(900);
        if (!_prewarm) return; // a real open took over in the meantime
        _prewarm = false;
        SetState(NotchState.Hidden);
    }

    void CancelPrewarm()
    {
        _prewarm = false;
        if (!IsVisible) return;
        Left = (ScreenWidthDip - Width) / 2;
        if (_settings.Visualizer) _viz.Start();
    }

    public void SetState(NotchState s)
    {
        if (s == State && (IsVisible || s == NotchState.Hidden)) return;
        if (_prewarm && !_inPrewarm) CancelPrewarm();
        var previous = State;
        State = s;
        if (s == NotchState.Expanded && previous != NotchState.Expanded) _sp.RequestRefresh(0);

        if (s != NotchState.Hidden && !IsVisible)
        {
            if (_hwnd != IntPtr.Zero) Scale = Native.GetDpiForWindow(_hwnd) / 96.0;
            _trimTimer.Stop();
            Left = _prewarm ? -12000 : (ScreenWidthDip - Width) / 2;
            Top = 0;
            _w.Snap(HiddenW);
            _h.Snap(0);
            ApplyNowPlaying();
            SnapTransitions();
            Show();
            Native.SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // TOPMOST, no move/size/activate
            if (_settings.Visualizer && !_prewarm) _viz.Start();
        }

        (_w.Target, _h.Target) = s switch
        {
            NotchState.Expanded => (ExpW, ExpandedHeight),
            NotchState.Peek => (PeekW, PeekH),
            _ => (HiddenW, 0.0),
        };
        _geomStale = true;
        SetFrameRate(fast: true);
        if (s == NotchState.Hidden) _hiddenAtTicks = Environment.TickCount64;
        StartLoop();
    }

    void SnapTransitions()
    {
        // a freshly opened notch shows the current track straight away, with no slide
        ShowArt(_media.Current.Art, animate: false);
        _artT = 1;
        _textT = 1;
        TitleClip.Opacity = 1;
        ArtistText.Opacity = 1;
        _playMix = _playTarget;
        ApplyPlayMix();
        _marqueeDirty = true;

        // open straight into the current track's colours; gliding is only for changes while visible
        _accentCur = _accentTarget;
        Array.Copy(_waveTarget, _waveCur, _waveCur.Length);
    }

    void HideNow()
    {
        StopLoop();
        _viz.Stop();
        _w.Snap(HiddenW);
        _h.Snap(0);
        ExpHost.Opacity = 0;
        PeekHost.Opacity = 0;
        ResetPage();
        Hide();
        ScheduleTrim();
    }

    public void ScheduleTrim()
    {
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    // ---- render loop (only runs while visible) --------------------------------------------

    void StartLoop()
    {
        if (_rendering) return;
        _rendering = true;
        _last = _clock.Elapsed;
        _lastFrameTicks = Environment.TickCount64;
        HookRendering(_fastFrames);
        _frameTimer.Start();
    }

    // While animating, frames come from WPF's own render loop (CompositionTarget.Rendering), which follows the display
    // refresh rate; a DispatcherTimer cannot do better than ~15.6 ms steps. The timer stays as a fallback (and drives the
    // ~30 fps maintenance frames once everything has settled).
    bool _renderHooked;
    TimeSpan _lastRenderTime;

    void HookRendering(bool on)
    {
        if (on == _renderHooked) return;
        _renderHooked = on;
        if (on) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
    }

    bool _clockOn;
    System.Windows.Media.Animation.DoubleAnimation? _clockAnim;

    void SetFrameClock(bool on)
    {
        if (on == _clockOn) return;
        _clockOn = on;
        if (on)
        {
            _clockAnim ??= MakeClockAnimation();
            FrameClock.BeginAnimation(OpacityProperty, _clockAnim);
        }
        else FrameClock.BeginAnimation(OpacityProperty, null);
    }

    static System.Windows.Media.Animation.DoubleAnimation MakeClockAnimation()
    {
        var a = new System.Windows.Media.Animation.DoubleAnimation(0.0, 0.004, TimeSpan.FromSeconds(1))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(a, Native.DisplayRefreshRate());
        return a;
    }

    void SetFrameRate(bool fast)
    {
        _fastFrames = fast;
        HookRendering(fast && _rendering);
        SetFrameClock(fast && _rendering);
        var interval = TimeSpan.FromMilliseconds(fast ? 50 : 33);
        if (_frameTimer.Interval != interval) _frameTimer.Interval = interval;
    }

    /// <summary>
    /// Called every couple of seconds by the controller. Recovers from "window visible but nothing is happening".
    /// </summary>
    public void Watchdog()
    {
        if (!IsVisible) return;
        long now = Environment.TickCount64;

        if (!_rendering)
        {
            Log.Write($"watchdog: window visible but the frame loop was stopped (state={State}); restarting it");
            _geomStale = true;
            StartLoop();
            return;
        }

        if (now - _lastFrameTicks > 1500)
        {
            Log.Write($"watchdog: no frame for {now - _lastFrameTicks} ms (state={State}); restarting the timer");
            _frameTimer.Stop();
            _geomStale = true;
            _lastFrameTicks = now;
            _frameTimer.Start();
            return;
        }

        if (State == NotchState.Hidden && now - _hiddenAtTicks > 2500)
        {
            Log.Write("watchdog: window never finished closing; hiding it");
            HideNow();
        }
    }

    void StopLoop()
    {
        HookRendering(false);
        SetFrameClock(false);
        _frameTimer.Stop();
        _rendering = false;
    }

    static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    static double Smooth(double v) { v = Clamp01(v); return v * v * (3 - 2 * v); }
    static double EaseOut(double v) { v = 1 - Clamp01(v); return 1 - v * v * v; }
    static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
    static Color LerpColor(Color a, Color b, double t) => Color.FromRgb(Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));

    // ---- frame-pacing diagnostics (run with --perf; results go to log.txt) ---------------------------
    public static bool PerfLog;
    bool _lastFrameAnimating;
    long _perfPrevT0;
    readonly List<double> _perfInterval = new(), _perfWork = new();

    void OnFrame(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs re)
        {
            if (re.RenderingTime == _lastRenderTime) return; // WPF sometimes raises Rendering twice per frame
            _lastRenderTime = re.RenderingTime;
        }
        else if (_fastFrames && (_clock.Elapsed - _last).TotalMilliseconds < 100) return; // timer is only a fallback while Rendering is running

        if (!PerfLog) { FrameBody(); return; }

        long t0 = Stopwatch.GetTimestamp();
        FrameBody();
        long t1 = Stopwatch.GetTimestamp();

        double toMs = 1000.0 / Stopwatch.Frequency;
        if ((t1 - t0) * toMs > 6)
            Log.Write($"PERF slow frame: {(t1 - t0) * toMs:F1} ms (state {State}, animating {_lastFrameAnimating}, gc0/1/2 = {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)})");
        if (_lastFrameAnimating)
        {
            if (_perfPrevT0 != 0) _perfInterval.Add((t0 - _perfPrevT0) * toMs);
            _perfWork.Add((t1 - t0) * toMs);
        }
        else if (_perfWork.Count > 0)
        {
            LogPerf();
        }
        _perfPrevT0 = t0;
    }

    void LogPerf()
    {
        static string Stats(List<double> v)
        {
            if (v.Count == 0) return "n/a";
            var sorted = new List<double>(v);
            sorted.Sort();
            double mean = 0; foreach (var x in v) mean += x; mean /= v.Count;
            return $"mean {mean:F1} / p95 {sorted[(int)(sorted.Count * 0.95)]:F1} / max {sorted[^1]:F1}";
        }
        double span = 0; foreach (var x in _perfInterval) span += x;
        double fps = span > 0 ? _perfInterval.Count / (span / 1000.0) : 0;
        string first(List<double> v) => string.Join(", ", v.Take(6).Select(x => x.ToString("F1")));
        Log.Write($"PERF {State}: {_perfWork.Count} animated frames, {fps:F0} fps | interval ms {Stats(_perfInterval)} | work ms {Stats(_perfWork)} | first work [{first(_perfWork)}] first intervals [{first(_perfInterval)}]");
        _perfInterval.Clear();
        _perfWork.Clear();
    }

    void FrameBody()
    {
        var now = _clock.Elapsed;
        _lastFrameTicks = Environment.TickCount64;
        double dt = Math.Clamp((now - _last).TotalSeconds, 0.001, 0.1);
        _last = now;

        bool closing = State == NotchState.Hidden;
        double k = closing ? 380 : 300, c = closing ? 39 : 25;
        _w.Step(dt, k, c);
        _h.Step(dt, k, c);
        _w.SettleIfClose();
        _h.SettleIfClose();
        bool moving = !(_w.IsSettled && _h.IsSettled);
        bool animating = moving || _wasMoving || _geomStale || _qDragging || _rowsAnimating;
        _lastFrameAnimating = animating;
        SetFrameRate(animating); // fast while resizing, ~30 fps once settled

        double h = Math.Max(0, _h.Value), w = Math.Max(40, _w.Value);
        if (closing && h < 1.0)
        {
            HideNow();
            return;
        }

        double t = Clamp01((h - PeekH) / (ExpH - PeekH));

        // silhouette, clips and staged reveal only change while the notch is resizing
        if (moving || _wasMoving || _geomStale)
        {
            double corner = 14 + 12 * t;
            double x0 = CX - w / 2;
            NotchPath.Data = Geo.Notch(CX, w, h, Ear, corner);
            UpdateHitRegion(w, h);
            ExpHost.Clip = Geo.Body(x0 - ExpLeft, w, h, corner);
            PeekHost.Clip = Geo.Body(x0 - PeekLeft, w, h, corner);
            ExpHost.Opacity = Clamp01(t / 0.22);
            PeekHost.Opacity = Clamp01((h - 14) / 20) * (1 - Clamp01(t * 2.5));

            double rArt = Smooth((t - 0.22) / 0.3), rText = Smooth((t - 0.32) / 0.3);
            double rProg = Smooth((t - 0.42) / 0.3), rCtl = Smooth((t - 0.50) / 0.3);
            ArtHost.Opacity = rArt;      ArtShift.Y = (1 - rArt) * 10;
            TextCol.Opacity = rText;     TextShift.Y = (1 - rText) * 10;
            ProgRow.Opacity = rProg;     ProgShift.Y = (1 - rProg) * 10;
            Controls.Opacity = rCtl;     ControlsShift.Y = (1 - rCtl) * 10;
        }
        _wasMoving = moving;
        _geomStale = false;

        // colours glide toward their targets (album accent, neon wave tinted by the album)
        double a = 1 - Math.Exp(-6 * dt);
        _accentCur = LerpColor(_accentCur, _accentTarget, a);
        _accent.Color = LerpColor(_accentCur, Colors.White, 0.4);
        double aw = 1 - Math.Exp(-3 * dt);
        for (int i = 0; i < _waveCur.Length; i++) _waveCur[i] = LerpColor(_waveCur[i], _waveTarget[i], aw);

        _viz.Update(dt);
        float energy = _viz.Energy;
        bool drawBars = energy > 0.002f || _barsLastEnergy > 0.002f; // skip redraws while silent
        _barsLastEnergy = energy;

        if (ExpHost.Opacity > 0.01)
        {
            // an open page fully covers the player, so don't keep redrawing the wave, bars and progress underneath it
            bool covered = _pageMix > 0.98 && !_settingsPage; // the settings page shows the background, so keep it live
            if (!covered)
            {
                UpdateAmbient(now.TotalSeconds);
                if (drawBars) Bars.InvalidateVisual();
                UpdateProgress();
                UpdateMarquee(now.TotalSeconds);
            }
            UpdateTransitions(dt);
            UpdatePageAndToast(dt, now);
        }

        if (PeekHost.Opacity > 0.01)
        {
            if (drawBars) PeekBars.InvalidateVisual();
            PeekGlowBottom.Color = Color.FromArgb(0x3C, _accentCur.R, _accentCur.G, _accentCur.B);
        }

        _playMix += (_playTarget - _playMix) * (1 - Math.Exp(-14 * dt));
        if (Math.Abs(_playTarget - _playMix) < 0.01) _playMix = _playTarget;
        ApplyPlayMix();

        double volAge = (now - _volAt).TotalSeconds;
        VolPill.Opacity = volAge < 1.1 ? 1 : Clamp01(1 - (volAge - 1.1) / 0.35);
    }

    void UpdateAmbient(double seconds)
    {
        bool on = _settings.Blobs;
        var blobsVis = on ? Visibility.Visible : Visibility.Collapsed;
        if (Ambient.Visibility != blobsVis) Ambient.Visibility = blobsVis;
        var dotsVis = _settings.DotGrid ? Visibility.Visible : Visibility.Collapsed;
        if (DotGrid.Visibility != dotsVis) DotGrid.Visibility = dotsVis;
        if (!on) return;

        bool reduce = _settings.ReduceMotion;
        bool colorsChanged = false;
        for (int i = 0; i < _waveCur.Length; i++)
            if (_waveCur[i] != _waveDrawn[i]) { colorsChanged = true; break; }

        // with reduced motion the wave is only redrawn when its colours change
        if (!reduce || colorsChanged)
        {
            Ambient.Update(reduce ? 0 : seconds, _waveCur);
            Array.Copy(_waveCur, _waveDrawn, _waveCur.Length);
        }
    }

    void ApplyPlayMix()
    {
        PlayIcon.Opacity = 1 - _playMix;
        PauseIcon.Opacity = _playMix;
        double ps = 0.7 + 0.3 * (1 - _playMix), qs = 0.7 + 0.3 * _playMix;
        PlayScale.ScaleX = PlayScale.ScaleY = ps;
        PauseScale.ScaleX = PauseScale.ScaleY = qs;
    }

    // ---- now playing ----------------------------------------------------------------------

    static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    void ApplyNowPlaying()
    {
        if (!PerfLog) { ApplyNowPlayingCore(); return; }
        long t0 = Stopwatch.GetTimestamp();
        ApplyNowPlayingCore();
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        if (ms > 3) Log.Write($"PERF ApplyNowPlaying took {ms:F1} ms (state {State})");
    }

    void ApplyNowPlayingCore()
    {
        var np = _media.Current;
        bool has = np.HasSession && np.Title.Length > 0;
        bool visible = IsVisible;

        string title = has ? np.Title : "Nothing playing";
        bool trackChanged = title != TitleText.Text;
        TitleText.Text = title;
        if (trackChanged && State == NotchState.Expanded) _sp.RequestRefresh(700); // liked state for the new track

        ArtistText.Inlines.Clear();
        if (has)
        {
            ArtistText.Inlines.Add(new Run(np.Artist));
            if (np.Album.Length > 0 && np.Album != np.Title)
                ArtistText.Inlines.Add(new Run("  ·  " + np.Album) { Foreground = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)) });
        }
        else ArtistText.Inlines.Add(new Run("Start something in Spotify"));

        PeekText.Text = has ? (np.Artist.Length > 0 ? $"{np.Title}  ·  {np.Artist}" : np.Title) : "Nothing playing";
        if (trackChanged)
        {
            _marqueeDirty = true;
            if (visible && has)
            {
                _textT = 0;
                TitleClip.Opacity = 0;
                ArtistText.Opacity = 0;
            }
        }

        ShowArt(np.Art, visible);
        PrevBtn.IsEnabled = NextBtn.IsEnabled = PlayBtn.IsEnabled = np.HasSession;

        _playTarget = np.IsPlaying ? 1 : 0;
        if (!visible)
        {
            _playMix = _playTarget;
            ApplyPlayMix();
        }

        _accentTarget = np.Art != null ? np.Accent : ArtColor.Fallback;
        UpdateWaveTargets();

        _shownDurSec = -1;
        _shownPosSec = -1;
        UpdateProgress();
    }

    void ShowArt(BitmapSource? art, bool animate)
    {
        PeekArt.Source = art;
        if (ReferenceEquals(art, _shownArt)) return;

        var old = _shownArt;
        _shownArt = art;

        if (animate && art != null)
        {
            // slide in the direction of the skip; a plain fade if the track changed on its own
            bool recentSkip = Environment.TickCount64 - _skipAtTicks < 2500;
            _artDir = old != null && recentSkip ? _skipDir : 0;
            ArtPrev.Source = old;
            ArtPrev.Opacity = old != null ? 1 : 0;
            ArtImage.Source = art;
            ArtImage.Opacity = 0;
            _artT = 0;
        }
        else
        {
            ArtImage.Source = art;
            ArtImage.Opacity = 1;
            ArtNewShift.X = 0;
            ArtPrev.Source = null;
            ArtPrev.Opacity = 0;
            _artT = 1;
        }
        _skipDir = 0;
    }

    void UpdateTransitions(double dt)
    {
        if (_artT < 1)
        {
            _artT = Math.Min(1, _artT + dt / 0.38);
            double e = EaseOut(_artT), dist = 30 * _artDir;
            ArtNewShift.X = (1 - e) * dist;
            ArtImage.Opacity = e;
            ArtPrevShift.X = -e * dist;
            ArtPrev.Opacity = 1 - e;
            if (_artT >= 1)
            {
                ArtPrev.Source = null;
                ArtPrev.Opacity = 0;
                ArtImage.Opacity = 1;
                ArtNewShift.X = 0;
            }
        }

        if (_textT < 1)
        {
            _textT = Math.Min(1, _textT + dt / 0.35);
            double e = EaseOut(_textT);
            TitleClip.Opacity = e;
            ArtistText.Opacity = e;
        }
    }

    /// <summary>Titles too long for the column scroll back and forth instead of being cut with "...".</summary>
    void UpdateMarquee(double seconds)
    {
        if (_marqueeDirty && TitleClip.ActualWidth > 0)
        {
            TitleText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _titleOverflow = Math.Max(0, TitleText.DesiredSize.Width - TitleClip.ActualWidth);
            _marqueeDirty = false;
            _marqueeStart = seconds;
            // soft fade at both edges only while the title is actually scrolling
            TitleClip.OpacityMask = _titleOverflow >= 2 ? MarqueeMask : null;
        }

        if (_titleOverflow < 2)
        {
            TitleMove.X = 0;
            return;
        }

        const double pause = 2.2, speed = 38;
        double scroll = _titleOverflow / speed;
        double cycle = 2 * (pause + scroll);
        double ph = (seconds - _marqueeStart) % cycle;
        double x;
        if (ph < pause) x = 0;
        else if (ph < pause + scroll) x = -_titleOverflow * Smooth((ph - pause) / scroll);
        else if (ph < 2 * pause + scroll) x = -_titleOverflow;
        else x = -_titleOverflow * (1 - Smooth((ph - 2 * pause - scroll) / scroll));
        TitleMove.X = x;
    }

    void UpdateProgress()
    {
        var np = _media.Current;
        double dur = np.Duration.TotalSeconds;
        var pos = _scrubbing ? TimeSpan.FromSeconds(_scrubFrac * dur) : np.PositionNow(DateTimeOffset.UtcNow);
        double frac = dur > 0 ? Clamp01(pos.TotalSeconds / dur) : 0;

        double tw = Track.ActualWidth;
        ProgFill.Width = tw * frac;
        ThumbMove.X = tw * frac - 5;

        int ps = (int)pos.TotalSeconds, ds = (int)dur;
        if (ps != _shownPosSec) { _shownPosSec = ps; PosText.Text = Fmt(pos); }
        if (ds != _shownDurSec) { _shownDurSec = ds; DurText.Text = Fmt(np.Duration); }
    }

    // ---- background settings page --------------------------------------------------------------------

    static readonly Brush PageOpaqueBrush = FrozenBrush(Color.FromArgb(0xFF, 5, 5, 7));
    // see-through so the background you are editing stays visible behind the settings rows
    static readonly Brush PageGlassBrush = FrozenBrush(Color.FromArgb(0x45, 5, 5, 7));
    bool _settingsPage;

    void WireBackgroundSettings()
    {
        SettingsBtn.Click += (_, _) => _ = OpenPageAsync(PageKind.Settings);
        DotsToggle.Click += (_, _) => { _settings.DotGrid = DotsToggle.IsChecked == true; _settings.Save(); };
        BlobsToggle.Click += (_, _) => { _settings.Blobs = BlobsToggle.IsChecked == true; _settings.Save(); UpdateBlobControlsEnabled(); };
        StillToggle.Click += (_, _) => { _settings.ReduceMotion = StillToggle.IsChecked == true; _settings.Save(); };
        ColorsDefault.Click += (_, _) => SetBlobColors("Default");
        ColorsAlbum.Click += (_, _) => SetBlobColors("Album");
    }

    void SetBlobColors(string mode)
    {
        _settings.BlobColors = mode;
        _settings.Save();
        UpdateWaveTargets(); // the blobs glide to the new colours
    }

    void SyncSettingsUi()
    {
        DotsToggle.IsChecked = _settings.DotGrid;
        BlobsToggle.IsChecked = _settings.Blobs;
        StillToggle.IsChecked = _settings.ReduceMotion;
        bool album = _settings.BlobColors == "Album";
        ColorsAlbum.IsChecked = album;
        ColorsDefault.IsChecked = !album;
        UpdateBlobControlsEnabled();
    }

    void UpdateBlobControlsEnabled()
    {
        bool on = BlobsToggle.IsChecked == true;
        BlobColorRow.IsEnabled = StillRow.IsEnabled = on;
        FadeTo(BlobColorRow, on ? 1 : 0.4, 160);
        FadeTo(StillRow, on ? 1 : 0.4, 160);
    }

    /// <summary>Blob colours: the neon trio leaning toward the cover ("Default"), or the cover's own palette ("Album").</summary>
    void UpdateWaveTargets()
    {
        var np = _media.Current;
        bool hasArt = np.Art != null;
        bool album = _settings.BlobColors == "Album" && hasArt;
        var tint = hasArt ? np.Accent : Color.FromRgb(128, 128, 128); // grey = no tint
        for (int i = 0; i < Neon.Length; i++)
            _waveTarget[i] = album ? np.Palette[i] : ArtColor.TintToward(Neon[i], tint, AlbumTint);
    }

    static void FadeTo(UIElement element, double to, double ms)
    {
        element.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        });
    }

    static void FadeBrush(Brush brush, double to, double ms)
    {
        brush.BeginAnimation(Brush.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        });
    }

    // ---- Spotify Web API extras (heart, shuffle, repeat, devices, queue) ----------------------------

    static readonly Brush DimBrush = FrozenBrush(Color.FromArgb(0xBF, 255, 255, 255));
    static readonly Brush GreenBrush = FrozenBrush(Color.FromRgb(0x1E, 0xD7, 0x60));
    static Brush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    enum PageKind { None, Devices, Queue, Settings }
    PageKind _page;
    double _pageMix, _pageTarget;
    TimeSpan _toastAt = TimeSpan.FromDays(-1);

    void WireSpotify()
    {
        HeartBtn.Click += (_, _) => _ = _sp.ToggleLikeAsync();
        ShuffleBtn.Click += (_, _) => _ = _sp.ToggleShuffleAsync();
        RepeatBtn.Click += (_, _) => _ = _sp.CycleRepeatAsync();
        ConnectBtn.Click += (_, _) => _ = _sp.ConnectAsync();
        DevicesBtn.Click += (_, _) => _ = OpenPageAsync(PageKind.Devices);
        QueueBtn.Click += (_, _) => _ = OpenPageAsync(PageKind.Queue);
        PageBack.Click += (_, _) => ClosePage();
        QueueApplyBtn.Click += (_, _) => _ = ApplyQueueAsync();
        QueueResetBtn.Click += (_, _) => ResetQueueEdits();
        _sp.Changed += ApplySpotifyState;
        _sp.Toast += ShowToast;
        ApplySpotifyState();
    }

    void ApplySpotifyState()
    {
        if (!PerfLog) { ApplySpotifyStateCore(); return; }
        long t0 = Stopwatch.GetTimestamp();
        ApplySpotifyStateCore();
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        if (ms > 3) Log.Write($"PERF ApplySpotifyState took {ms:F1} ms (state {State})");
    }

    void ApplySpotifyStateCore()
    {
        bool connected = _sp.Enabled && _sp.Connected;
        var shown = connected ? Visibility.Visible : Visibility.Collapsed;
        LeftExtras.Visibility = RightExtras.Visibility = ShuffleBtn.Visibility = RepeatBtn.Visibility = shown;
        ConnectBtn.Visibility = _sp.Enabled && !_sp.Connected ? Visibility.Visible : Visibility.Collapsed;

        HeartIcon.Filled = _sp.Liked;
        HeartBtn.Foreground = _sp.Liked ? GreenBrush : DimBrush;
        HeartBtn.IsEnabled = _sp.TrackUri != null;
        ShuffleBtn.Foreground = _sp.Shuffle ? GreenBrush : DimBrush;
        RepeatBtn.Foreground = _sp.Repeat != "off" ? GreenBrush : DimBrush;
        RepeatIcon.Data = (Geometry)FindResource(_sp.Repeat == "track" ? "Icon.Repeat1" : "Icon.Repeat");
    }

    void ShowToast(string message)
    {
        ToastText.Text = message;
        _toastAt = _clock.Elapsed;
    }

    async Task OpenPageAsync(PageKind kind)
    {
        if (_qApplying) return;
        ResetQueueState();
        _page = kind;
        PageTitle.Text = kind == PageKind.Devices ? "Devices" : kind == PageKind.Settings ? "Background" : "Up next";
        PageCount.Text = "";
        PageFooter.Visibility = Visibility.Collapsed;
        PageScroll.ScrollToTop();
        bool settings = kind == PageKind.Settings;
        _settingsPage = settings;
        PageScroll.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsRows.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        PagePanel.Background = settings ? PageGlassBrush : PageOpaqueBrush;
        PagePanel.Visibility = Visibility.Visible;
        _pageTarget = 1;
        SetTall(kind == PageKind.Queue);
        if (settings)
        {
            SyncSettingsUi();
            return;
        }
        SetRows(MakeNote("Loading\u2026"));

        if (kind == PageKind.Devices)
        {
            var (list, error) = await _sp.GetDevicesAsync();
            if (_page != kind) return;
            if (list == null) SetRows(MakeNote(error ?? "Couldn't load devices"));
            else if (list.Count == 0) SetRows(MakeNote("No devices found. Open Spotify on a device first."));
            else SetRows(list.Take(4).Select(MakeDeviceRow).ToArray());
        }
        else
        {
            await LoadQueueAsync();
        }
    }

    void ClosePage()
    {
        if (_qApplying) return;
        _page = PageKind.None;
        _pageTarget = 0;
        SetTall(false);
        if (_qDragging)
        {
            _qDragging = false;
            _dragGrip?.ReleaseMouseCapture();
        }
    }

    void ResetPage()
    {
        _page = PageKind.None;
        _pageMix = _pageTarget = 0;
        _tall = false;
        PagePanel.Opacity = 0;
        PagePanel.Visibility = Visibility.Collapsed;
        PageRows.Children.Clear();
        PageFooter.Visibility = Visibility.Collapsed;
        _settingsPage = false;
        MainContent.Opacity = 1;
        SettingsRows.Visibility = Visibility.Collapsed;
        PageScroll.Visibility = Visibility.Visible;
        ResetQueueState();
    }

    void SetRows(params UIElement[] rows)
    {
        PageRows.Children.Clear();
        foreach (var r in rows) PageRows.Children.Add(r);
    }

    static UIElement MakeNote(string text) => new TextBlock
    {
        Text = text,
        FontSize = 12.5,
        Foreground = DimBrush,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(4, 10, 4, 0),
    };

    UIElement MakeDeviceRow(DeviceInfo d)
    {
        Brush tint = d.Active ? GreenBrush : Brushes.White;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new LucideIcon { Data = (Geometry)FindResource(DeviceIconKey(d.Type)), Width = 17, Height = 17, Foreground = tint });

        var name = new TextBlock
        {
            Text = d.Name,
            FontSize = 13.5,
            FontWeight = d.Active ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = tint,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        if (d.Active)
        {
            var check = new LucideIcon { Data = (Geometry)FindResource("Icon.Check"), Width = 16, Height = 16, Foreground = GreenBrush };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }

        var btn = new Button { Style = (Style)FindResource("RowBtn"), Height = 31, Content = grid };
        btn.Click += async (_, _) => await TransferToAsync(d);
        return btn;
    }

    // ---- queue editing (drag to reorder, remove, apply) --------------------------------------------------
    // Spotify has no reorder/remove endpoint for the queue, so edits happen on this local list and "Apply order"
    // restarts playback with [current song, ...the list] (see SpotifyFeatures.ApplyQueueAsync).

    const double QRowH = 46;

    sealed class QRow
    {
        public QueueItem Item = null!;
        public Border Root = null!;
        public readonly TranslateTransform Shift = new();
        public readonly ScaleTransform Lift = new(1, 1);
        public double TargetShift, CollapseT;
        public readonly SolidColorBrush HoverFill = new(Color.FromArgb(0x1F, 255, 255, 255)) { Opacity = 0 };
    }

    readonly List<QRow> _qRows = new();
    readonly List<QRow> _qCollapsing = new();
    List<QueueItem> _qOriginal = new();
    bool _qDragging, _qApplying, _rowsAnimating;
    QRow? _dragRow;
    UIElement? _dragGrip;
    int _dragFrom;
    double _dragStartY;

    static readonly Brush RowHoverBrush = FrozenBrush(Color.FromArgb(0x1F, 255, 255, 255));
    static readonly Brush RowDragBrush = FrozenBrush(Color.FromArgb(0x38, 255, 255, 255));

    void SetTall(bool on)
    {
        if (_tall == on) return;
        _tall = on;
        if (State != NotchState.Expanded) return;
        _h.Target = ExpandedHeight;
        _geomStale = true;
        SetFrameRate(fast: true);
        StartLoop();
    }

    void ResetQueueState()
    {
        if (_qDragging) _dragGrip?.ReleaseMouseCapture();
        _qDragging = false;
        _dragRow = null;
        _dragGrip = null;
        _qRows.Clear();
        _qCollapsing.Clear();
        _qOriginal = new List<QueueItem>();
    }

    async Task LoadQueueAsync()
    {
        var (list, error) = await _sp.GetQueueAsync();
        if (_page != PageKind.Queue) return;

        if (list == null)
        {
            SetRows(MakeNote(error ?? "Couldn't load the queue"));
            PageFooter.Visibility = Visibility.Collapsed;
            PageCount.Text = "";
            return;
        }

        _qOriginal = list;
        if (list.Count == 0)
        {
            SetRows(MakeNote("The queue is empty."));
            PageFooter.Visibility = Visibility.Collapsed;
            PageCount.Text = "";
            return;
        }
        BuildQueueRows(list);
    }

    void BuildQueueRows(IReadOnlyList<QueueItem> items)
    {
        _qRows.Clear();
        _qCollapsing.Clear();
        PageRows.Children.Clear();
        foreach (var q in items)
        {
            var row = MakeQueueRow(q);
            _qRows.Add(row);
            PageRows.Children.Add(row.Root);
        }
        UpdateQueueFooter();
    }

    QRow MakeQueueRow(QueueItem q)
    {
        var row = new QRow { Item = q };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var grip = new Border { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = "Drag to reorder" };
        grip.Child = new LucideIcon
        {
            Data = (Geometry)FindResource("Icon.Grip"),
            Width = 16,
            Height = 16,
            Foreground = DimBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grip.MouseLeftButtonDown += (_, e) => BeginDrag(row, grip, e);
        grip.MouseLeftButtonUp += (_, _) => EndDrag();
        grip.LostMouseCapture += (_, _) => EndDrag();
        grid.Children.Add(grip);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
        text.Children.Add(new TextBlock
        {
            Text = q.Title,
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = q.Artist,
            FontSize = 12,
            Foreground = DimBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var remove = new Button
        {
            Style = (Style)FindResource("SmallBtn"),
            Width = 28,
            Height = 28,
            ToolTip = "Remove from this list",
            Content = new LucideIcon { Data = (Geometry)FindResource("Icon.X"), Width = 14, Height = 14 },
        };
        remove.Click += (_, _) => RemoveRow(row);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);

        row.Root = new Border
        {
            Height = QRowH,
            CornerRadius = new CornerRadius(10),
            Background = row.HoverFill,
            Child = grid,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { row.Lift, row.Shift } },
        };
        row.Root.MouseEnter += (_, _) => { if (!_qDragging) FadeBrush(row.HoverFill, 1, 130); };
        row.Root.MouseLeave += (_, _) => { if (_dragRow != row) FadeBrush(row.HoverFill, 0, 240); };
        return row;
    }

    void BeginDrag(QRow row, UIElement grip, MouseButtonEventArgs e)
    {
        if (_qDragging || _qApplying || _qCollapsing.Count > 0) return;
        _dragRow = row;
        _dragGrip = grip;
        _dragFrom = _qRows.IndexOf(row);
        _dragStartY = Mouse.GetPosition(PageRows).Y;
        _qDragging = true;
        grip.CaptureMouse();

        Panel.SetZIndex(row.Root, 5);
        row.Lift.ScaleX = row.Lift.ScaleY = 1.03;
        row.Root.Background = RowDragBrush;
        e.Handled = true;
    }

    void EndDrag()
    {
        if (!_qDragging || _dragRow == null) return;
        var row = _dragRow;
        _qDragging = false;
        _dragGrip?.ReleaseMouseCapture();
        _dragRow = null;

        int n = _qRows.Count, from = _dragFrom;
        double dy = row.Shift.Y;
        int target = Math.Clamp((int)Math.Round(from + dy / QRowH), 0, n - 1);

        if (target != from)
        {
            _qRows.RemoveAt(from);
            _qRows.Insert(target, row);
            PageRows.Children.RemoveAt(from);
            PageRows.Children.Insert(target, row.Root);
        }

        // the other rows are already drawn where their new slots are; the dragged row glides in from where the pointer left it
        foreach (var r in _qRows)
            if (r != row) { r.Shift.Y = 0; r.TargetShift = 0; }
        row.Shift.Y = dy - (target - from) * QRowH;
        row.TargetShift = 0;
        row.Lift.ScaleX = row.Lift.ScaleY = 1;
        row.Root.Background = row.HoverFill;
        FadeBrush(row.HoverFill, row.Root.IsMouseOver ? 1 : 0, 180);
        Panel.SetZIndex(row.Root, 0);
        UpdateQueueFooter();
    }

    void RemoveRow(QRow row)
    {
        if (_qDragging || _qApplying || !_qRows.Contains(row)) return;
        _qRows.Remove(row);
        row.Root.IsHitTestVisible = false;
        _qCollapsing.Add(row);
        UpdateQueueFooter();
    }

    void ResetQueueEdits()
    {
        if (_qApplying || _qDragging) return;
        BuildQueueRows(_qOriginal);
    }

    void UpdateQueueFooter()
    {
        if (_page != PageKind.Queue) return;
        bool dirty = !_qRows.Select(r => r.Item.Uri).SequenceEqual(_qOriginal.Select(q => q.Uri));
        PageFooter.Visibility = Visibility.Visible;
        QueueApplyBtn.Visibility = QueueResetBtn.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        PageCount.Text = _qRows.Count == 0 ? "" : _qRows.Count == 1 ? "1 song" : $"{_qRows.Count} songs";
        PageHint.Text = _qApplying ? "Applying\u2026"
            : dirty ? "Replaces the playlist/album with this list"
            : _qOriginal.Count >= 18 ? "Drag the handle to reorder. Spotify shows only the next ~20 songs."
            : "Drag the handle to reorder.";
    }

    async Task ApplyQueueAsync()
    {
        if (_qApplying || _qDragging || _qCollapsing.Count > 0) return;
        _qApplying = true;
        QueueApplyBtn.IsEnabled = QueueResetBtn.IsEnabled = false;
        UpdateQueueFooter();

        var order = _qRows.Select(r => r.Item).ToList();
        string? error = await _sp.ApplyQueueAsync(order);

        _qApplying = false;
        QueueApplyBtn.IsEnabled = QueueResetBtn.IsEnabled = true;
        if (error != null)
        {
            ShowToast(error);
            UpdateQueueFooter();
            return;
        }

        ShowToast("Queue order applied");
        if (_page == PageKind.Queue)
        {
            await Task.Delay(800); // give Spotify a moment before reading the new queue back
            if (_page == PageKind.Queue) await LoadQueueAsync();
        }
    }

    /// <summary>Per-frame: the dragged row follows the pointer, the others glide out of the way, removed rows collapse.</summary>
    void UpdateQueueRows(double dt)
    {
        bool animating = false;

        if (_qDragging && _dragRow != null)
        {
            int n = _qRows.Count;
            double dy = Mouse.GetPosition(PageRows).Y - _dragStartY;
            dy = Math.Clamp(dy, -_dragFrom * QRowH, (n - 1 - _dragFrom) * QRowH);
            _dragRow.Shift.Y = dy;

            int target = Math.Clamp((int)Math.Round(_dragFrom + dy / QRowH), 0, n - 1);
            for (int i = 0; i < n; i++)
            {
                var r = _qRows[i];
                if (r == _dragRow) continue;
                r.TargetShift = _dragFrom < target && i > _dragFrom && i <= target ? -QRowH
                              : _dragFrom > target && i >= target && i < _dragFrom ? QRowH
                              : 0;
            }

            // scroll the list when the pointer is near its top or bottom edge
            double py = Mouse.GetPosition(PageScroll).Y, vh = PageScroll.ActualHeight;
            if (py < 34) PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - Math.Min(16, (34 - py) * 0.6));
            else if (py > vh - 34) PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset + Math.Min(16, (py - (vh - 34)) * 0.6));
            animating = true;
        }

        double k = 1 - Math.Exp(-22 * dt);
        foreach (var r in _qRows)
        {
            if (_qDragging && r == _dragRow) continue;
            double d = r.TargetShift - r.Shift.Y;
            if (Math.Abs(d) > 0.3) { r.Shift.Y += d * k; animating = true; }
            else if (r.Shift.Y != r.TargetShift) r.Shift.Y = r.TargetShift;
        }

        for (int i = _qCollapsing.Count - 1; i >= 0; i--)
        {
            var r = _qCollapsing[i];
            r.CollapseT = Math.Min(1, r.CollapseT + dt / 0.2);
            double e = Smooth(r.CollapseT);
            r.Root.Height = QRowH * (1 - e);
            r.Root.Opacity = 1 - e;
            animating = true;
            if (r.CollapseT >= 1)
            {
                PageRows.Children.Remove(r.Root);
                _qCollapsing.RemoveAt(i);
            }
        }

        _rowsAnimating = animating;
    }

    static string DeviceIconKey(string type) => type switch
    {
        "Computer" => "Icon.Monitor",
        "Smartphone" or "Tablet" => "Icon.Smartphone",
        "TV" => "Icon.Tv",
        _ => "Icon.Speaker",
    };

    async Task TransferToAsync(DeviceInfo d)
    {
        if (d.Active) { ClosePage(); return; }
        string? error = await _sp.TransferAsync(d);
        ShowToast(error ?? $"Playing on {d.Name}");
        ClosePage();
        _sp.RequestRefresh(900);
    }

    void UpdatePageAndToast(double dt, TimeSpan now)
    {
        UpdateQueueRows(dt);
        if (_pageMix != _pageTarget)
        {
            _pageMix += (_pageTarget - _pageMix) * (1 - Math.Exp(-16 * dt));
            if (Math.Abs(_pageTarget - _pageMix) < 0.01) _pageMix = _pageTarget;
            PagePanel.Opacity = _pageMix;
            if (_settingsPage) MainContent.Opacity = 1 - _pageMix;
            if (_pageMix == 0)
            {
                _settingsPage = false;
                MainContent.Opacity = 1;
                SettingsRows.Visibility = Visibility.Collapsed;
                PageScroll.Visibility = Visibility.Visible;
                PagePanel.Visibility = Visibility.Collapsed;
                PageRows.Children.Clear();
                _qRows.Clear();
                _qCollapsing.Clear();
            }
        }

        double age = (now - _toastAt).TotalSeconds;
        ToastPill.Opacity = age < 2.6 ? Math.Min(1, age / 0.12) : Clamp01(1 - (age - 2.6) / 0.35);
    }

    // ---- interaction ---------------------------------------------------------------------

    void UpdateScrub(MouseEventArgs e)
    {
        double tw = Track.ActualWidth;
        if (tw <= 0) return;
        _scrubFrac = Clamp01(e.GetPosition(Track).X / tw);
    }

    void EndScrub(MouseEventArgs e)
    {
        if (!_scrubbing) return;
        UpdateScrub(e);
        _scrubbing = false;
        Track.ReleaseMouseCapture();
        FadeTo(Thumb, Track.IsMouseOver ? 1 : 0, 180);
        var dur = _media.Current.Duration;
        if (dur > TimeSpan.Zero) _ = _media.SeekAsync(TimeSpan.FromSeconds(_scrubFrac * dur.TotalSeconds));
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_page != PageKind.None) return; // a page is open: let its list scroll instead of changing the volume
        var v = VolumeService.Adjust(e.Delta > 0 ? 0.05f : -0.05f, _settings.SourceFilter);
        if (v is float vol)
        {
            VolText.Text = $"{(int)Math.Round(vol * 100)}%";
            _volAt = _clock.Elapsed;
        }
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotkey();
        _viz.Dispose();
        base.OnClosed(e);
    }
}
