using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpotifyNotch;

public sealed record ApiResult(bool Ok, int Status, JsonElement Json, string? Error)
{
    public static readonly ApiResult NotConnected = new(false, 0, default, "Not connected to Spotify");
    public bool HasJson => Json.ValueKind != JsonValueKind.Undefined;
}

/// <summary>Thin wrapper over the Spotify Web API endpoints the notch uses.</summary>
public sealed class SpotifyApi
{
    const string Base = "https://api.spotify.com/v1";
    readonly SpotifyAuth _auth;
    readonly HttpClient _http;

    public SpotifyApi(SpotifyAuth auth, HttpClient http)
    {
        _auth = auth;
        _http = http;
    }

    // ---- endpoints --------------------------------------------------------------------------

    public Task<ApiResult> GetPlayerAsync() => SendAsync(HttpMethod.Get, "/me/player");
    public Task<ApiResult> GetDevicesAsync() => SendAsync(HttpMethod.Get, "/me/player/devices");
    public Task<ApiResult> GetQueueAsync() => SendAsync(HttpMethod.Get, "/me/player/queue");

    public Task<ApiResult> IsSavedAsync(string uri) =>
        SendAsync(HttpMethod.Get, "/me/library/contains?uris=" + Uri.EscapeDataString(uri));

    public Task<ApiResult> SetSavedAsync(string uri, bool saved) =>
        SendAsync(saved ? HttpMethod.Put : HttpMethod.Delete, "/me/library?uris=" + Uri.EscapeDataString(uri));

    public Task<ApiResult> TransferAsync(string deviceId, bool play) =>
        SendAsync(HttpMethod.Put, "/me/player", new { device_ids = new[] { deviceId }, play });

    public Task<ApiResult> SetShuffleAsync(bool on) =>
        SendAsync(HttpMethod.Put, "/me/player/shuffle?state=" + (on ? "true" : "false"));

    /// <param name="state">off, context or track</param>
    public Task<ApiResult> SetRepeatAsync(string state) =>
        SendAsync(HttpMethod.Put, "/me/player/repeat?state=" + state);

    /// <summary>Start playback of exactly these tracks (the only way to "reorder" the queue: Spotify has no reorder endpoint).</summary>
    public Task<ApiResult> PlayUrisAsync(IReadOnlyList<string> uris, int positionMs) =>
        SendAsync(HttpMethod.Put, "/me/player/play", new { uris, position_ms = positionMs });

    public Task<ApiResult> PauseAsync() => SendAsync(HttpMethod.Put, "/me/player/pause");

    // ---- plumbing -----------------------------------------------------------------------------

    async Task<ApiResult> SendAsync(HttpMethod method, string path, object? body = null, bool retry = true)
    {
        string? token = await _auth.GetAccessTokenAsync();
        if (token == null) return ApiResult.NotConnected;

        try
        {
            using var req = new HttpRequestMessage(method, Base + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            else if (method != HttpMethod.Get) req.Content = new StringContent("");

            using var res = await _http.SendAsync(req);

            if (res.StatusCode == HttpStatusCode.Unauthorized && retry)
            {
                _auth.Invalidate();
                return await SendAsync(method, path, body, retry: false);
            }

            if (res.StatusCode == (HttpStatusCode)429 && retry)
            {
                double wait = res.Headers.RetryAfter?.Delta?.TotalSeconds ?? 1;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 0.5, 3)));
                return await SendAsync(method, path, body, retry: false);
            }

            string text = await res.Content.ReadAsStringAsync();
            JsonElement json = default;
            string? error = null;
            if (text.Length > 0 && (text[0] == '{' || text[0] == '['))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    json = doc.RootElement.Clone();
                    if (!res.IsSuccessStatusCode && json.ValueKind == JsonValueKind.Object &&
                        json.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object &&
                        e.TryGetProperty("message", out var m))
                        error = m.GetString();
                }
                catch (JsonException) { }
            }

            int status = (int)res.StatusCode;
            // 204 on the player endpoints just means "nothing is playing"; everything else that fails is worth a log line
            if (!res.IsSuccessStatusCode && status != 404)
                Log.Write($"spotify {method} {path.Split('?')[0]} -> {status} {error ?? text}");
            return new ApiResult(res.IsSuccessStatusCode, status, json, error ?? (res.IsSuccessStatusCode ? null : text));
        }
        catch (Exception ex)
        {
            Log.Write($"spotify {method} {path.Split('?')[0]} failed: {ex.Message}");
            return new ApiResult(false, 0, default, "Can't reach Spotify");
        }
    }
}
