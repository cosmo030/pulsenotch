using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SpotifyNotch;

public sealed record DeviceInfo(string Id, string Name, string Type, bool Active);
public sealed record QueueItem(string Uri, string Title, string Artist);

/// <summary>
/// Web API features on top of the local media session: like, shuffle, repeat, devices, queue.
/// All state changes are raised on the UI thread. Everything is hidden until the user connects Spotify.
/// </summary>
public sealed class SpotifyFeatures
{
    readonly SpotifyAuth _auth;
    readonly SpotifyApi _api;
    readonly Dispatcher _ui = Application.Current.Dispatcher;
    readonly List<QueueItem> _demoQueue = new()
    {
        new("spotify:track:d01", "Instant Crush (feat. Julian Casablancas)", "Daft Punk"),
        new("spotify:track:d02", "Let It Happen", "Tame Impala"),
        new("spotify:track:d03", "A Very Long Song Title That Needs To Be Trimmed Somewhere", "Some Artist With A Long Name"),
        new("spotify:track:d04", "Rich Spirit", "Kendrick Lamar"),
        new("spotify:track:d05", "Tribe (with J. Cole)", "Bas"),
        new("spotify:track:d06", "The Magic Key", "One-T"),
        new("spotify:track:d07", "Shut up My Moms Calling", "Hotel Ugly"),
        new("spotify:track:d08", "GHOST RIDE", "BLK ODYSSEY"),
        new("spotify:track:d09", "Dark Red", "Steve Lacy"),
        new("spotify:track:d10", "FUCKING YOUNG / PERFECT", "Tyler, The Creator"),
        new("spotify:track:d11", "Do What I Want", "Kid Cudi"),
        new("spotify:track:d12", "Blinding Lights", "The Weeknd"),
        new("spotify:track:d13", "Redbone", "Childish Gambino"),
        new("spotify:track:d14", "Nights", "Frank Ocean"),
    };

    readonly bool _demo; // --demo: fake connected state, fake devices/queue, never talks to Spotify
    int _queued;
    bool _refreshing;

    public bool Enabled => _demo || _auth.Enabled;
    public bool Connected => _demo || _auth.IsConnected;

    public string? TrackUri { get; private set; }
    public bool Liked { get; private set; }
    public bool Shuffle { get; private set; }
    public string Repeat { get; private set; } = "off";
    public string? ActiveDevice { get; private set; }
    public bool? DeviceIsPlaying { get; private set; }

    public event Action? Changed;
    public event Action<string>? Toast;

    public SpotifyFeatures(string clientId, bool demo = false)
    {
        _demo = demo;
        if (demo) { TrackUri = "spotify:track:demo"; Shuffle = true; Repeat = "context"; }
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _auth = new SpotifyAuth(clientId.Trim(), http);
        _api = new SpotifyApi(_auth, http);
        _auth.ConnectionChanged += () => _ui.BeginInvoke(() =>
        {
            if (!Connected) { TrackUri = null; Liked = false; }
            Changed?.Invoke();
        });
    }

    // ---- connection ---------------------------------------------------------------------------

    public async Task ConnectAsync()
    {
        string? error = await _auth.LoginAsync();
        if (error != null) { Toast?.Invoke(error); return; }
        Toast?.Invoke("Connected to Spotify");
        Changed?.Invoke();
        RequestRefresh(0);
    }

    public void Disconnect()
    {
        _auth.Logout();
        Toast?.Invoke("Disconnected from Spotify");
    }

    // ---- state refresh ------------------------------------------------------------------------

    /// <summary>Re-read player state and whether the current track is liked (coalesced; UI thread only).</summary>
    public void RequestRefresh(int delayMs)
    {
        if (!Connected) return;
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        _ = RefreshLaterAsync(delayMs);
    }

    async Task RefreshLaterAsync(int delayMs)
    {
        if (delayMs > 0) await Task.Delay(delayMs);
        Interlocked.Exchange(ref _queued, 0);
        await RefreshAsync();
    }

    async Task RefreshAsync()
    {
        if (!Connected || _refreshing || _demo) return;
        _refreshing = true;
        try
        {
            var res = await _api.GetPlayerAsync();
            if (res.Status == 401 || res.Status == 0) return;

            if (res.Ok && res.HasJson && res.Json.ValueKind == JsonValueKind.Object)
            {
                var p = res.Json;
                Shuffle = p.TryGetProperty("shuffle_state", out var sh) && sh.ValueKind == JsonValueKind.True;
                Repeat = p.TryGetProperty("repeat_state", out var rp) ? rp.GetString() ?? "off" : "off";
                DeviceIsPlaying = p.TryGetProperty("is_playing", out var ip) ? ip.ValueKind == JsonValueKind.True : null;
                ActiveDevice = p.TryGetProperty("device", out var dv) && dv.ValueKind == JsonValueKind.Object &&
                               dv.TryGetProperty("name", out var dn) ? dn.GetString() : null;
                TrackUri = p.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object &&
                           item.TryGetProperty("uri", out var u) ? u.GetString() : null;
            }
            else
            {
                // 204: nothing playing / no active device
                TrackUri = null;
                Liked = false;
            }

            if (TrackUri != null)
            {
                var c = await _api.IsSavedAsync(TrackUri);
                Liked = c.Ok && c.HasJson && c.Json.ValueKind == JsonValueKind.Array && c.Json.GetArrayLength() > 0 &&
                        c.Json[0].ValueKind == JsonValueKind.True;
            }
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Write("spotify refresh: " + ex.Message); }
        finally { _refreshing = false; }
    }

    // ---- actions -----------------------------------------------------------------------------

    public async Task ToggleLikeAsync()
    {
        if (!Connected) return;
        if (_demo) { Liked = !Liked; Changed?.Invoke(); Toast?.Invoke(Liked ? "Added to Liked Songs" : "Removed from Liked Songs"); return; }
        if (TrackUri == null) { Toast?.Invoke("Nothing to like yet"); return; }

        bool target = !Liked;
        Liked = target;
        Changed?.Invoke();

        var res = await _api.SetSavedAsync(TrackUri, target);
        if (!res.Ok)
        {
            Liked = !target;
            Changed?.Invoke();
            Toast?.Invoke(Friendly(res));
            return;
        }
        Toast?.Invoke(target ? "Added to Liked Songs" : "Removed from Liked Songs");
    }

    public async Task ToggleShuffleAsync()
    {
        if (!Connected) return;
        if (_demo) { Shuffle = !Shuffle; Changed?.Invoke(); return; }
        bool target = !Shuffle;
        Shuffle = target;
        Changed?.Invoke();
        var res = await _api.SetShuffleAsync(target);
        if (!res.Ok) { Shuffle = !target; Changed?.Invoke(); Toast?.Invoke(Friendly(res)); }
    }

    public async Task CycleRepeatAsync()
    {
        if (!Connected) return;
        if (_demo) { Repeat = Repeat switch { "off" => "context", "context" => "track", _ => "off" }; Changed?.Invoke(); return; }
        string previous = Repeat;
        string next = previous switch { "off" => "context", "context" => "track", _ => "off" };
        Repeat = next;
        Changed?.Invoke();
        var res = await _api.SetRepeatAsync(next);
        if (!res.Ok) { Repeat = previous; Changed?.Invoke(); Toast?.Invoke(Friendly(res)); }
    }

    public async Task<(List<DeviceInfo>? list, string? error)> GetDevicesAsync()
    {
        if (_demo)
            return (new List<DeviceInfo>
            {
                new("1", "My PC", "Computer", true), new("2", "iPhone", "Smartphone", false),
                new("3", "Living Room", "Speaker", false), new("4", "Samsung TV", "TV", false),
            }, null);
        var res = await _api.GetDevicesAsync();
        if (!res.Ok) return (null, Friendly(res));

        var list = new List<DeviceInfo>();
        if (res.HasJson && res.Json.TryGetProperty("devices", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in arr.EnumerateArray())
            {
                string id = d.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                if (id.Length == 0) continue; // restricted devices have no id
                list.Add(new DeviceInfo(
                    id,
                    d.TryGetProperty("name", out var n) ? n.GetString() ?? "Device" : "Device",
                    d.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                    d.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True));
            }
        }
        return (list, null);
    }

    /// <summary>Move playback to a device. Returns an error message, or null on success.</summary>
    public async Task<string?> TransferAsync(DeviceInfo device)
    {
        if (_demo) return null;
        var res = await _api.TransferAsync(device.Id, DeviceIsPlaying ?? true);
        return res.Ok ? null : Friendly(res);
    }

    public async Task<(List<QueueItem>? list, string? error)> GetQueueAsync()
    {
        if (_demo) return (new List<QueueItem>(_demoQueue), null);
        var res = await _api.GetQueueAsync();
        if (!res.Ok) return (null, Friendly(res));

        var list = new List<QueueItem>();
        if (res.HasJson && res.Json.TryGetProperty("queue", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in arr.EnumerateArray())
            {
                string title = q.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string uri = q.TryGetProperty("uri", out var uu) ? uu.GetString() ?? "" : "";
                string artist = "";
                if (q.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0 &&
                    artists[0].TryGetProperty("name", out var an))
                    artist = an.GetString() ?? "";
                else if (q.TryGetProperty("show", out var show) && show.TryGetProperty("name", out var sn))
                    artist = sn.GetString() ?? ""; // podcast episode
                if (title.Length > 0) list.Add(new QueueItem(uri, title, artist));
            }
        }
        return (list, null);
    }

    /// <summary>
    /// Spotify has no "reorder queue" endpoint, so the edited order is applied by restarting playback with
    /// [current song, ...order] at the current position. Returns an error message, or null on success.
    /// </summary>
    public async Task<string?> ApplyQueueAsync(IReadOnlyList<QueueItem> order)
    {
        if (_demo)
        {
            await Task.Delay(400);
            _demoQueue.Clear();
            _demoQueue.AddRange(order);
            return null;
        }
        if (!Connected) return "Not connected to Spotify";

        var read = DateTimeOffset.UtcNow;
        var snap = await _api.GetPlayerAsync();
        if (!snap.Ok) return Friendly(snap);
        if (!snap.HasJson || snap.Json.ValueKind != JsonValueKind.Object) return "Nothing is playing right now";

        var p = snap.Json;
        string? current = p.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object &&
                          item.TryGetProperty("uri", out var cu) ? cu.GetString() : null;
        if (current == null) return "Nothing is playing right now";

        int progress = p.TryGetProperty("progress_ms", out var pm) && pm.ValueKind == JsonValueKind.Number ? pm.GetInt32() : 0;
        bool playing = p.TryGetProperty("is_playing", out var ip) && ip.ValueKind == JsonValueKind.True;
        bool shuffle = p.TryGetProperty("shuffle_state", out var sh) && sh.ValueKind == JsonValueKind.True;

        var uris = new List<string> { current };
        foreach (var q in order)
            if (q.Uri.Length > 0 && !uris.Contains(q.Uri)) uris.Add(q.Uri);

        if (shuffle)
        {
            // with shuffle on, Spotify would re-shuffle the list we are about to play
            var off = await _api.SetShuffleAsync(false);
            if (!off.Ok) return Friendly(off);
            Shuffle = false;
            Changed?.Invoke();
        }

        int position = progress + (playing ? (int)(DateTimeOffset.UtcNow - read).TotalMilliseconds + 200 : 0);
        var res = await _api.PlayUrisAsync(uris, position);
        if (!res.Ok) return Friendly(res);

        if (!playing)
        {
            await Task.Delay(300);
            await _api.PauseAsync(); // keep it paused if it was paused
        }
        RequestRefresh(700);
        return null;
    }

    static string Friendly(ApiResult r) => r.Status switch
    {
        401 => "Reconnect Spotify (login expired)",
        403 => "Spotify refused: " + (r.Error ?? "not allowed"),
        404 => "No active Spotify device",
        0 => r.Error ?? "Can't reach Spotify",
        _ => r.Error ?? $"Spotify error {r.Status}",
    };
}
