using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace SpotifyNotch;

/// <summary>
/// Decides when the notch opens and closes: hover at the top-centre of the screen,
/// the global hotkey, and the brief "peek" on track changes.
/// </summary>
public sealed class NotchController
{
    const double ZoneHalfWidth = 140; // DIPs either side of screen centre that count as "top centre"

    readonly Settings _settings;
    readonly NotchWindow _window;
    readonly MediaService _media;

    // While the notch is visible: 30 Hz on the UI thread (also drives auto-close).
    readonly DispatcherTimer _poll = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
    // While hidden: a cheap 20 Hz background check that never touches the UI thread until the cursor is in the zone.
    readonly Timer _hoverTimer;
    readonly DispatcherTimer _housekeeping = new() { Interval = TimeSpan.FromSeconds(2) };

    long _dwellStart = -1, _peekUntil, _lastInside;
    bool _everInside, _hoverRunning;
    volatile bool _fullscreen;

    public NotchController(Settings settings, NotchWindow window, MediaService media)
    {
        _settings = settings;
        _window = window;
        _media = media;

        _hoverTimer = new Timer(_ => HoverTick(), null, Timeout.Infinite, Timeout.Infinite);
        _poll.Tick += (_, _) => VisibleTick();
        _housekeeping.Tick += (_, _) => Housekeeping();
        _media.TrackChanged += OnTrackChanged;
        _media.SourceChanged += UpdateTimers; // an app with media appeared or went away: arm or disarm the hover check
        _housekeeping.Start();
        Housekeeping();
    }

    public void ApplySettings() => UpdateTimers();

    public void Toggle()
    {
        if (_window.State == NotchState.Expanded) SetState(NotchState.Hidden);
        else Open();
    }

    void Open()
    {
        _everInside = false;
        _lastInside = Environment.TickCount64;
        SetState(NotchState.Expanded);
    }

    void SetState(NotchState s)
    {
        _dwellStart = -1;
        _window.SetState(s);
        UpdateTimers();
    }

    void OnTrackChanged()
    {
        if (!_settings.PeekOnTrackChange || _fullscreen || !_media.Current.IsPlaying) return;
        if (_window.State == NotchState.Expanded) return;
        _peekUntil = Environment.TickCount64 + 3200;
        SetState(NotchState.Peek);
    }

    void UpdateTimers()
    {
        bool visible = _window.State != NotchState.Hidden;
        if (visible && !_poll.IsEnabled) _poll.Start();
        else if (!visible && _poll.IsEnabled) _poll.Stop();

        bool hoverActive = !visible && _settings.HoverEnabled && _media.HasAnySession && !_fullscreen;
        if (hoverActive != _hoverRunning)
        {
            _hoverRunning = hoverActive;
            _hoverTimer.Change(hoverActive ? 0 : Timeout.Infinite, hoverActive ? 50 : Timeout.Infinite);
        }
    }

    void Housekeeping()
    {
        _window.Watchdog();
        _fullscreen = Native.IsFullscreenAppActive();
        UpdateTimers();
    }

    /// <summary>Hidden state. Runs on a thread-pool thread.</summary>
    void HoverTick()
    {
        if (_window.State != NotchState.Hidden) return;
        if (!_settings.HoverEnabled || !_media.HasAnySession || _fullscreen) { _dwellStart = -1; return; }

        Native.GetCursorPos(out var p);
        double cx = p.X / _window.Scale;
        double center = _window.ScreenWidthDip / 2;
        bool inZone = p.Y <= 1 && Math.Abs(cx - center) <= ZoneHalfWidth && !Native.LeftButtonDown();
        if (!inZone) { _dwellStart = -1; return; }

        long now = Environment.TickCount64;
        if (_dwellStart < 0) { _dwellStart = now; return; }
        if (now - _dwellStart < _settings.HoverDelayMs) return;

        _dwellStart = -1;
        _window.Dispatcher.BeginInvoke(() => { if (_window.State == NotchState.Hidden) Open(); });
    }

    /// <summary>Peek / Expanded states. Runs on the UI thread.</summary>
    void VisibleTick()
    {
        long now = Environment.TickCount64;
        Native.GetCursorPos(out var p);
        double sc = _window.Scale;
        double cx = p.X / sc, cy = p.Y / sc;

        switch (_window.State)
        {
            case NotchState.Peek:
            {
                var r = _window.TargetRect(NotchState.Peek);
                bool over = cx >= r.Left - 8 && cx <= r.Right + 8 && cy <= r.Bottom + 8;
                if (_settings.HoverEnabled && over) Open();
                else if (now > _peekUntil) SetState(NotchState.Hidden);
                break;
            }

            case NotchState.Expanded:
            {
                var r = _window.TargetRect(NotchState.Expanded);
                bool inside = cx >= r.Left - 16 && cx <= r.Right + 16 && cy <= r.Bottom + 16;
                if (inside || _window.Interacting)
                {
                    _lastInside = now;
                    _everInside = true;
                }
                // Opened by hotkey and never hovered: give a few seconds, then tuck away.
                long grace = _everInside ? 450 : 4500;
                if (now - _lastInside > grace) SetState(NotchState.Hidden);
                break;
            }

            default:
                UpdateTimers();
                break;
        }
    }
}
