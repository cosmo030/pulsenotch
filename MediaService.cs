using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SpotifyNotch;

public sealed class NowPlaying
{
    public bool HasSession;
    public string SourceId = "", SourceName = "", ProcessName = "";
    public bool IsSpotify;
    public bool CanNext = true, CanPrevious = true, CanSeek = true;
    public string Title = "", Artist = "", Album = "";
    public bool IsPlaying;
    public TimeSpan Duration, Position;
    public DateTimeOffset Stamp = DateTimeOffset.UtcNow;
    public BitmapSource? Art;
    public Color Accent = ArtColor.Fallback;
    public Color[] Palette = { ArtColor.Fallback, ArtColor.Fallback, ArtColor.Fallback };

    /// <summary>The source reports no duration (live stream, or an app without a timeline).</summary>
    public bool NoDuration => HasSession && Title.Length > 0 && Duration <= TimeSpan.Zero;

    // the source is part of the key so cover art and palette can never leak from one app to another
    public string Key => $"{SourceId}\u001f{Title}\u001f{Artist}\u001f{Album}";

    public TimeSpan PositionNow(DateTimeOffset now)
    {
        var p = Position + (IsPlaying ? now - Stamp : TimeSpan.Zero);
        if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p;
    }
}

/// <summary>One app that currently has a Windows media session, as shown on the Sources page.</summary>
public sealed record SourceInfo(string Id, string Name, string Title, string Artist, bool IsPlaying, bool IsActive, bool Hidden);

/// <summary>
/// Reads now-playing info from the Windows media sessions (SMTC) of every app (Spotify, browsers playing YouTube,
/// Apple Music, VLC, ...) and sends transport commands to the active one. No login or API key needed.
/// </summary>
public sealed class MediaService
{
    sealed class TrackedSession
    {
        public GlobalSystemMediaTransportControlsSession Session = null!;
        public string Id = "", Name = "", Process = "";
        public bool Playing;
        public long StartedPlayingAt;
        public Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? OnProps;
        public Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? OnPlayback;
        public Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs>? OnTimeline;
    }

    readonly Settings _settings;
    readonly Dispatcher _ui = Application.Current.Dispatcher;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _lock = new();
    readonly List<TrackedSession> _sessions = new();

    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    TrackedSession? _active;
    volatile bool _hasAny;

    int _queued;
    string _lastKey = "";
    string _lastSourceId = "";
    string _artKey = "";
    string _artRetryKey = "";
    int _artRetries;
    BitmapSource? _art;
    Color _artAccent = ArtColor.Fallback;
    Color[] _artPalette = { ArtColor.Fallback, ArtColor.Fallback, ArtColor.Fallback };
    bool _demo;

    GlobalSystemMediaTransportControlsSession? ActiveSession => _active?.Session;

    public NowPlaying Current { get; private set; } = new();

    /// <summary>True when at least one eligible app has a media session (cheap; replaces scanning for a Spotify process).</summary>
    public bool HasAnySession => _demo || _hasAny;

    public event Action? Changed;
    public event Action? TrackChanged;
    /// <summary>The active app changed (a different app started playing, or you pinned/hid one).</summary>
    public event Action? SourceChanged;

    public MediaService(Settings settings) => _settings = settings;

    // ---- sessions -----------------------------------------------------------------------------

    public async Task StartAsync()
    {
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _mgr.SessionsChanged += (_, _) => Reconcile();
        Reconcile();
    }

    /// <summary>Match our tracked list to what Windows reports: subscribe to new sessions, drop vanished ones.</summary>
    void Reconcile()
    {
        if (_mgr == null) return;
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> live;
        try { live = _mgr.GetSessions(); }
        catch (Exception ex) { Log.Write("sessions: " + ex.Message); return; }

        lock (_lock)
        {
            for (int i = _sessions.Count - 1; i >= 0; i--)
            {
                var ts = _sessions[i];
                if (live.Any(s => ReferenceEquals(s, ts.Session))) continue;
                Unsubscribe(ts);
                _sessions.RemoveAt(i);
            }

            foreach (var s in live)
            {
                if (_sessions.Any(t => ReferenceEquals(t.Session, s))) continue;
                var (name, process) = DescribeApp(s.SourceAppUserModelId);
                var ts = new TrackedSession { Session = s, Id = s.SourceAppUserModelId, Name = name, Process = process };
                try { ts.Playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
                catch { }
                if (ts.Playing) ts.StartedPlayingAt = Environment.TickCount64;

                ts.OnProps = (_, _) => { if (ReferenceEquals(_active, ts)) RequestRefresh(60); };
                ts.OnPlayback = (_, _) => OnPlaybackChanged(ts);
                ts.OnTimeline = (_, _) => { if (ReferenceEquals(_active, ts)) RequestRefresh(150); };
                s.MediaPropertiesChanged += ts.OnProps;
                s.PlaybackInfoChanged += ts.OnPlayback;
                s.TimelinePropertiesChanged += ts.OnTimeline;
                _sessions.Add(ts);
            }
        }
        Reevaluate(forceRefresh: true);
    }

    static void Unsubscribe(TrackedSession ts)
    {
        try
        {
            if (ts.OnProps != null) ts.Session.MediaPropertiesChanged -= ts.OnProps;
            if (ts.OnPlayback != null) ts.Session.PlaybackInfoChanged -= ts.OnPlayback;
            if (ts.OnTimeline != null) ts.Session.TimelinePropertiesChanged -= ts.OnTimeline;
        }
        catch { }
    }

    void OnPlaybackChanged(TrackedSession ts)
    {
        bool playing = false;
        try { playing = ts.Session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { }

        lock (_lock)
        {
            if (playing && !ts.Playing) ts.StartedPlayingAt = Environment.TickCount64;
            ts.Playing = playing;
        }
        Reevaluate(forceRefresh: ReferenceEquals(_active, ts));
    }

    bool IsHidden(string id) => _settings.IgnoredApps.Any(a => string.Equals(a, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Auto policy: a pinned app wins if it exists; otherwise whatever is playing (the most recently started if several, and the
    /// current one sticks while it keeps playing); with nothing playing, stay on the last active app.
    /// </summary>
    TrackedSession? Choose(List<TrackedSession> eligible)
    {
        string pinned = _settings.PinnedSource;
        if (!string.IsNullOrEmpty(pinned))
        {
            var pins = eligible.Where(s => string.Equals(s.Id, pinned, StringComparison.OrdinalIgnoreCase)).ToList();
            if (pins.Count > 0) return pins.FirstOrDefault(s => s.Playing) ?? (_active != null && pins.Contains(_active) ? _active : pins[0]);
        }

        var playing = eligible.Where(s => s.Playing).ToList();
        if (playing.Count > 0)
        {
            if (_active != null && playing.Contains(_active)) return _active;
            return playing.OrderByDescending(s => s.StartedPlayingAt).First();
        }

        if (_active != null && eligible.Contains(_active)) return _active;
        return eligible.OrderByDescending(s => s.StartedPlayingAt).FirstOrDefault();
    }

    void Reevaluate(bool forceRefresh = false)
    {
        bool changed;
        lock (_lock)
        {
            var eligible = _sessions.Where(s => !IsHidden(s.Id)).ToList();
            var next = Choose(eligible);
            changed = !ReferenceEquals(next, _active);
            _active = next;
            _hasAny = next != null;
        }

        if (changed) _ = _ui.BeginInvoke(() => SourceChanged?.Invoke());
        if (changed || forceRefresh) RequestRefresh(0);
    }

    // ---- reading the active session ---------------------------------------------------------------

    void RequestRefresh(int delayMs)
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            Interlocked.Exchange(ref _queued, 0);
            await RefreshAsync();
        });
    }

    async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var ts = _active;
            var s = ts?.Session;
            var np = new NowPlaying();
            bool artMissing = false;

            if (s != null && ts != null)
            {
                np.HasSession = true;
                np.SourceId = ts.Id;
                np.SourceName = ts.Name;
                np.ProcessName = ts.Process;
                np.IsSpotify = ts.Id.Contains("spotify", StringComparison.OrdinalIgnoreCase);
                try
                {
                    var props = await s.TryGetMediaPropertiesAsync();
                    np.Title = props.Title ?? "";
                    np.Artist = props.Artist ?? "";
                    np.Album = props.AlbumTitle ?? "";

                    var tl = s.GetTimelineProperties();
                    np.Duration = tl.EndTime - tl.StartTime;
                    np.Position = tl.Position - tl.StartTime;
                    var stamp = tl.LastUpdatedTime;
                    np.Stamp = stamp.Year > 2000 ? stamp : DateTimeOffset.UtcNow;

                    var info = s.GetPlaybackInfo();
                    np.IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                    var controls = info.Controls;
                    np.CanNext = np.IsSpotify || controls.IsNextEnabled;
                    np.CanPrevious = np.IsSpotify || controls.IsPreviousEnabled;
                    np.CanSeek = np.IsSpotify || controls.IsPlaybackPositionEnabled;

                    if (np.Key == _artKey && _art != null)
                    {
                        np.Art = _art;
                        np.Accent = _artAccent;
                        np.Palette = _artPalette;
                    }
                    else if (props.Thumbnail != null)
                    {
                        try
                        {
                            var (img, accent, palette) = await LoadArtAsync(props.Thumbnail);
                            _artKey = np.Key;
                            _art = img;
                            _artAccent = accent;
                            _artPalette = palette;
                            np.Art = img;
                            np.Accent = accent;
                            np.Palette = palette;
                        }
                        catch (Exception ex)
                        {
                            artMissing = true;
                            if (_artRetries == 0) Log.Write($"cover failed ({ts.Name}): {ex.GetType().Name} {ex.Message}");
                        }
                    }
                    else
                    {
                        artMissing = true;
                        if (_artRetries == 0) Log.Write($"no cover offered by {ts.Name} yet");
                    }
                }
                catch (Exception ex) { Log.Write($"refresh failed ({ts.Name}): {ex.GetType().Name} {ex.Message}"); }
            }

            if (NotchWindow.PerfLog)
                Log.Write($"PERF refresh: {np.SourceName} '{np.Title}' art={(np.Art != null)} playing={np.IsPlaying} canNext={np.CanNext} canSeek={np.CanSeek} dur={np.Duration.TotalSeconds:F0}s");

            bool sourceSwitched = np.SourceId != _lastSourceId;
            bool trackChanged = np.Title.Length > 0 && np.Key != _lastKey && !sourceSwitched; // no peek just because another app took over
            bool firstEver = _lastKey.Length == 0;
            if (np.Title.Length > 0) _lastKey = np.Key;
            _lastSourceId = np.SourceId;

            await _ui.InvokeAsync(() =>
            {
                Current = np;
                Changed?.Invoke();
                if (trackChanged && !firstEver) TrackChanged?.Invoke();
            });

            // covers often arrive a beat after the title; retry a few times, then give up (do not spin forever)
            if (_artRetryKey != np.Key) { _artRetryKey = np.Key; _artRetries = 0; }
            if (artMissing && np.Title.Length > 0 && _artRetries < 6)
            {
                _artRetries++;
                RequestRefresh(700 * _artRetries);
            }
        }
        finally { _gate.Release(); }
    }

    static async Task<(BitmapImage, Color, Color[])> LoadArtAsync(IRandomAccessStreamReference reference)
    {
        using var ras = await reference.OpenReadAsync();
        using var stream = ras.AsStreamForRead();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var bytes = ms.ToArray();
        if (bytes.Length == 0) throw new InvalidDataException("empty thumbnail");

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 256;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();
        return (bmp, ArtColor.FromBytes(bytes), ArtColor.PaletteFromBytes(bytes));
    }

    // ---- sources (for the Sources page) ---------------------------------------------------------

    /// <summary>Every app with a media session right now, with what it is playing (hidden apps included).</summary>
    public async Task<List<SourceInfo>> GetSourcesAsync()
    {
        if (_demo) return DemoSources();

        List<TrackedSession> snapshot;
        TrackedSession? active;
        lock (_lock) { snapshot = _sessions.ToList(); active = _active; }

        var list = new List<SourceInfo>();
        foreach (var ts in snapshot)
        {
            string title = "", artist = "";
            try
            {
                var props = await ts.Session.TryGetMediaPropertiesAsync();
                title = props.Title ?? "";
                artist = props.Artist ?? "";
            }
            catch { }
            bool hidden;
            lock (_lock) hidden = IsHidden(ts.Id);
            list.Add(new SourceInfo(ts.Id, ts.Name, title, artist, ts.Playing, ReferenceEquals(ts, active), hidden));
        }
        return list;
    }

    public string PinnedSource => _settings.PinnedSource;

    /// <summary>Pin one app (null or empty = automatic).</summary>
    public void Pin(string? id)
    {
        _settings.PinnedSource = id ?? "";
        _settings.Save();
        if (_demo) { DemoApply(); return; }
        Reevaluate(forceRefresh: true);
    }

    public void SetHidden(string id, bool hidden)
    {
        lock (_lock)
        {
            _settings.IgnoredApps.RemoveAll(a => string.Equals(a, id, StringComparison.OrdinalIgnoreCase));
            if (hidden)
            {
                _settings.IgnoredApps.Add(id);
                if (string.Equals(_settings.PinnedSource, id, StringComparison.OrdinalIgnoreCase)) _settings.PinnedSource = ""; // hiding wins over pinning
            }
        }
        _settings.Save();
        if (_demo) { DemoApply(); return; }
        Reevaluate(forceRefresh: true);
    }

    public int HiddenCount
    {
        get { lock (_lock) return _settings.IgnoredApps.Count; }
    }

    public void ShowAllHidden()
    {
        lock (_lock) _settings.IgnoredApps.Clear();
        _settings.Save();
        if (_demo) { DemoApply(); return; }
        Reevaluate(forceRefresh: true);
    }

    /// <summary>Friendly name and audio-process name for an app's media-session id.</summary>
    public static (string name, string process) DescribeApp(string appId)
    {
        string id = appId.ToLowerInvariant();
        (string key, string name, string process)[] known =
        {
            ("spotify", "Spotify", "Spotify"),
            ("brave", "Brave", "brave"),
            ("chrome", "Chrome", "chrome"),
            ("msedge", "Edge", "msedge"),
            ("edge", "Edge", "msedge"),
            ("firefox", "Firefox", "firefox"),
            ("opera", "Opera", "opera"),
            ("vivaldi", "Vivaldi", "vivaldi"),
            ("applemusic", "Apple Music", "AppleMusic"),
            ("itunes", "iTunes", "iTunes"),
            ("vlc", "VLC", "vlc"),
            ("tidal", "Tidal", "TIDAL"),
            ("deezer", "Deezer", "Deezer"),
            ("amazonmusic", "Amazon Music", "Amazon Music"),
            ("foobar", "foobar2000", "foobar2000"),
            ("musicbee", "MusicBee", "MusicBee"),
            ("mediaplayer", "Media Player", "Microsoft.Media.Player"),
        };
        foreach (var k in known)
            if (id.Contains(k.key, StringComparison.Ordinal)) return (k.name, k.process);

        // unknown app: tidy the id ("Contoso.Player_abc123!App" -> "Player")
        string clean = appId;
        int bang = clean.IndexOf('!');
        if (bang > 0) clean = clean[..bang];
        int under = clean.IndexOf('_');
        if (under > 0) clean = clean[..under];
        if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) clean = clean[..^4];
        int dot = clean.LastIndexOf('.');
        if (dot > 0 && dot < clean.Length - 1) clean = clean[(dot + 1)..];
        if (clean.Length == 0) clean = appId;
        return (clean, clean);
    }

    // ---- transport controls (always the active session) -------------------------------------------

    public async Task PlayPauseAsync()
    {
        var c = Current;
        var now = DateTimeOffset.UtcNow;
        c.Position = c.PositionNow(now);
        c.Stamp = now;
        c.IsPlaying = !c.IsPlaying;
        Changed?.Invoke();
        var s = ActiveSession;
        if (_demo || s == null) return;
        try { await s.TryTogglePlayPauseAsync(); } catch (Exception ex) { Log.Write("play/pause: " + ex.Message); }
    }

    public async Task NextAsync()
    {
        if (_demo) { DemoSkip(+1); return; }
        var s = ActiveSession;
        if (s == null) return;
        try { await s.TrySkipNextAsync(); } catch (Exception ex) { Log.Write("next: " + ex.Message); }
    }

    public async Task PreviousAsync()
    {
        if (_demo) { DemoSkip(-1); return; }
        var s = ActiveSession;
        if (s == null) return;
        try { await s.TrySkipPreviousAsync(); } catch (Exception ex) { Log.Write("previous: " + ex.Message); }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var c = Current;
        c.Position = position;
        c.Stamp = DateTimeOffset.UtcNow;
        Changed?.Invoke();
        var s = ActiveSession;
        if (_demo || s == null) return;
        try { await s.TryChangePlaybackPositionAsync(position.Ticks); } catch (Exception ex) { Log.Write("seek: " + ex.Message); }
    }

    // ---- demo mode (no real apps needed; for testing the UI) -----------------------------------

    int _demoIndex;
    NowPlaying[]? _demoTracks;      // Spotify tracks
    NowPlaying? _demoBrave;         // a paused video in a browser

    public void StartDemo()
    {
        _demo = true;
        _demoTracks = new[]
        {
            MakeDemoTrack("Blinding Lights", "The Weeknd", "After Hours", Color.FromRgb(0xFF, 0x6A, 0x3D), Color.FromRgb(0x8E, 0x1B, 0xC8), "Spotify", true),
            MakeDemoTrack("A Very Long Song Title That Needs To Scroll Across The Notch", "Some Artist", "Long Album Name", Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x0B, 0x3B, 0x8C), "Spotify", true),
        };
        _demoBrave = MakeDemoTrack("Easier If We Fly (Ending 1 OST)", "Slyje", "", Color.FromRgb(0x2E, 0x9B, 0xFF), Color.FromRgb(0xFF, 0x4D, 0x8D), "Brave", false);
        _demoBrave.IsPlaying = false;
        _demoBrave.CanNext = _demoBrave.CanPrevious = false;
        DemoApply();
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            await _ui.InvokeAsync(() => TrackChanged?.Invoke());
        });
    }

    /// <summary>Demo: pick the pinned fake source, else Spotify (the one "playing").</summary>
    void DemoApply()
    {
        if (_demoTracks == null || _demoBrave == null) return;
        string pinned = _settings.PinnedSource;
        bool braveHidden = IsHidden("Brave"), spotifyHidden = IsHidden("Spotify.exe");
        NowPlaying next;
        if (pinned == "Brave" && !braveHidden) next = _demoBrave;
        else if (spotifyHidden) next = braveHidden ? new NowPlaying() : _demoBrave;
        else next = _demoTracks[_demoIndex];
        bool switched = Current.SourceId != next.SourceId;
        Current = next;
        Changed?.Invoke();
        if (switched) SourceChanged?.Invoke();
    }

    List<SourceInfo> DemoSources()
    {
        var spot = _demoTracks![_demoIndex];
        bool braveActive = Current.SourceId == "Brave";
        return new List<SourceInfo>
        {
            new("Spotify.exe", "Spotify", spot.Title, spot.Artist, true, !braveActive, IsHidden("Spotify.exe")),
            new("Brave", "Brave", _demoBrave!.Title, _demoBrave.Artist, false, braveActive, IsHidden("Brave")),
        };
    }

    void DemoSkip(int step)
    {
        if (_demoTracks == null || !Current.IsSpotify) return;
        _demoIndex = (_demoIndex + step + _demoTracks.Length) % _demoTracks.Length;
        var t = _demoTracks[_demoIndex];
        t.Position = TimeSpan.Zero;
        t.Stamp = DateTimeOffset.UtcNow;
        Current = t;
        Changed?.Invoke();
        TrackChanged?.Invoke();
    }

    static NowPlaying MakeDemoTrack(string title, string artist, string album, Color from, Color to, string source, bool isSpotify)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(from, to, 45), null, new Rect(0, 0, 256, 256));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(120, 255, 220, 120)), null, new Point(90, 80), 70, 70);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 20, 10, 60)), null, new Point(180, 190), 90, 90);
        }
        var rtb = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);

        var (name, process) = DescribeApp(source);
        return new NowPlaying
        {
            HasSession = true,
            SourceId = isSpotify ? "Spotify.exe" : source,
            SourceName = name,
            ProcessName = process,
            IsSpotify = isSpotify,
            Title = title,
            Artist = artist,
            Album = album,
            IsPlaying = true,
            Duration = TimeSpan.FromSeconds(200),
            Position = TimeSpan.FromSeconds(42),
            Stamp = DateTimeOffset.UtcNow,
            Art = rtb,
            Accent = ArtColor.FromBytes(ms.ToArray()),
            Palette = ArtColor.PaletteFromBytes(ms.ToArray()),
        };
    }
}
