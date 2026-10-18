using System;
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
    public string Title = "", Artist = "", Album = "";
    public bool IsPlaying;
    public TimeSpan Duration, Position;
    public DateTimeOffset Stamp = DateTimeOffset.UtcNow;
    public BitmapSource? Art;
    public Color Accent = ArtColor.Fallback;
    public Color[] Palette = { ArtColor.Fallback, ArtColor.Fallback, ArtColor.Fallback };

    public string Key => $"{Title}\u001f{Artist}\u001f{Album}";

    public TimeSpan PositionNow(DateTimeOffset now)
    {
        var p = Position + (IsPlaying ? now - Stamp : TimeSpan.Zero);
        if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p;
    }
}

/// <summary>
/// Reads now-playing info from the Windows media session (SMTC) and sends transport commands to it.
/// No login / API key needed; works for the Spotify desktop app.
/// </summary>
public sealed class MediaService
{
    readonly string _filter;
    readonly Dispatcher _ui = Application.Current.Dispatcher;
    readonly SemaphoreSlim _gate = new(1, 1);

    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? _onProps;
    Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? _onPlayback;
    Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs>? _onTimeline;

    int _queued;
    string _lastKey = "";
    string _artKey = "";
    BitmapSource? _art;
    Color _artAccent = ArtColor.Fallback;
    Color[] _artPalette = { ArtColor.Fallback, ArtColor.Fallback, ArtColor.Fallback };
    bool _demo;

    public NowPlaying Current { get; private set; } = new();
    public event Action? Changed;
    public event Action? TrackChanged;

    public MediaService(string sourceFilter) => _filter = sourceFilter;

    public async Task StartAsync()
    {
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _mgr.SessionsChanged += (_, _) => PickSession();
        _mgr.CurrentSessionChanged += (_, _) => PickSession();
        PickSession();
    }

    void PickSession()
    {
        if (_mgr == null) return;
        GlobalSystemMediaTransportControlsSession? s = string.IsNullOrWhiteSpace(_filter)
            ? _mgr.GetCurrentSession()
            : _mgr.GetSessions().FirstOrDefault(x => x.SourceAppUserModelId.Contains(_filter, StringComparison.OrdinalIgnoreCase));

        if (_session?.SourceAppUserModelId == s?.SourceAppUserModelId && (_session != null) == (s != null))
        {
            RequestRefresh(0);
            return;
        }

        if (_session != null)
        {
            if (_onProps != null) _session.MediaPropertiesChanged -= _onProps;
            if (_onPlayback != null) _session.PlaybackInfoChanged -= _onPlayback;
            if (_onTimeline != null) _session.TimelinePropertiesChanged -= _onTimeline;
        }

        _session = s;
        if (s != null)
        {
            _onProps = (_, _) => RequestRefresh(60);
            _onPlayback = (_, _) => RequestRefresh(60);
            _onTimeline = (_, _) => RequestRefresh(150);
            s.MediaPropertiesChanged += _onProps;
            s.PlaybackInfoChanged += _onPlayback;
            s.TimelinePropertiesChanged += _onTimeline;
        }
        RequestRefresh(0);
    }

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
            var s = _session;
            var np = new NowPlaying();
            bool artMissing = false;

            if (s != null)
            {
                np.HasSession = true;
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

                    np.IsPlaying = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

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
                        catch { artMissing = true; }
                    }
                    else artMissing = true;
                }
                catch (Exception ex) { Log.Write("refresh failed: " + ex.Message); }
            }

            bool trackChanged = np.Title.Length > 0 && np.Key != _lastKey;
            bool firstEver = _lastKey.Length == 0;
            if (np.Title.Length > 0) _lastKey = np.Key;

            await _ui.InvokeAsync(() =>
            {
                Current = np;
                Changed?.Invoke();
                if (trackChanged && !firstEver) TrackChanged?.Invoke();
            });

            // Spotify often delivers cover art a beat after the title.
            if (artMissing && np.Title.Length > 0) RequestRefresh(700);
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

    // ---- transport controls -------------------------------------------------------------

    public async Task PlayPauseAsync()
    {
        var c = Current;
        var now = DateTimeOffset.UtcNow;
        c.Position = c.PositionNow(now);
        c.Stamp = now;
        c.IsPlaying = !c.IsPlaying;
        Changed?.Invoke();
        if (_demo || _session == null) return;
        try { await _session.TryTogglePlayPauseAsync(); } catch (Exception ex) { Log.Write("play/pause: " + ex.Message); }
    }

    public async Task NextAsync()
    {
        if (_demo) { DemoSkip(+1); return; }
        if (_session == null) return;
        try { await _session.TrySkipNextAsync(); } catch (Exception ex) { Log.Write("next: " + ex.Message); }
    }

    public async Task PreviousAsync()
    {
        if (_demo) { DemoSkip(-1); return; }
        if (_session == null) return;
        try { await _session.TrySkipPreviousAsync(); } catch (Exception ex) { Log.Write("previous: " + ex.Message); }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var c = Current;
        c.Position = position;
        c.Stamp = DateTimeOffset.UtcNow;
        Changed?.Invoke();
        if (_demo || _session == null) return;
        try { await _session.TryChangePlaybackPositionAsync(position.Ticks); } catch (Exception ex) { Log.Write("seek: " + ex.Message); }
    }

    // ---- demo mode (no Spotify needed; for testing the UI) -----------------------------------

    int _demoIndex;
    NowPlaying[]? _demoTracks;

    public void StartDemo()
    {
        _demo = true;
        _demoTracks = new[]
        {
            MakeDemoTrack("Blinding Lights", "The Weeknd", "After Hours", Color.FromRgb(0xFF, 0x6A, 0x3D), Color.FromRgb(0x8E, 0x1B, 0xC8)),
            MakeDemoTrack("A Very Long Song Title That Needs To Scroll Across The Notch", "Some Artist", "Long Album Name", Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x0B, 0x3B, 0x8C)),
        };
        Current = _demoTracks[0];
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            await _ui.InvokeAsync(() => TrackChanged?.Invoke());
        });
    }

    void DemoSkip(int step)
    {
        if (_demoTracks == null) return;
        _demoIndex = (_demoIndex + step + _demoTracks.Length) % _demoTracks.Length;
        var t = _demoTracks[_demoIndex];
        t.Position = TimeSpan.Zero;
        t.Stamp = DateTimeOffset.UtcNow;
        Current = t;
        Changed?.Invoke();
        TrackChanged?.Invoke();
    }

    static NowPlaying MakeDemoTrack(string title, string artist, string album, Color from, Color to)
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

        return new NowPlaying
        {
            HasSession = true,
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
