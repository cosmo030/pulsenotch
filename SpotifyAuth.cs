using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace SpotifyNotch;

/// <summary>
/// Spotify "Authorization Code with PKCE" login for a desktop app: no client secret.
/// Opens the user's browser, receives the redirect on a loopback port, and keeps the tokens
/// encrypted (Windows DPAPI, current user only) in %APPDATA%\SpotifyNotch.
/// </summary>
public sealed class SpotifyAuth
{
    public const int Port = 5543;
    public static string RedirectUri => $"http://127.0.0.1:{Port}/callback";

    const string AuthorizeUrl = "https://accounts.spotify.com/authorize";
    const string TokenUrl = "https://accounts.spotify.com/api/token";
    const string Scopes =
        "user-read-playback-state user-modify-playback-state user-read-currently-playing user-library-read user-library-modify";

    static string TokenPath => Path.Combine(Settings.Dir, "spotify-token.dat");

    internal sealed record TokenData(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

    readonly string _clientId;
    readonly HttpClient _http;
    readonly SemaphoreSlim _refreshGate = new(1, 1);
    TokenData? _token;
    bool _loggingIn;

    public bool Enabled => !string.IsNullOrWhiteSpace(_clientId);
    public bool IsConnected => _token != null;

    /// <summary>Raised when the connected/disconnected state changes (may fire on a background thread).</summary>
    public event Action? ConnectionChanged;

    public SpotifyAuth(string clientId, HttpClient http)
    {
        _clientId = clientId;
        _http = http;
        LoadToken();
    }

    // ---- login ----------------------------------------------------------------------------

    /// <summary>Runs the browser login. Returns null on success, otherwise a short error message.</summary>
    public async Task<string?> LoginAsync()
    {
        if (!Enabled) return "No Spotify Client ID in settings.json";
        if (_loggingIn) return "Login already in progress";
        _loggingIn = true;
        TcpListener? listener = null;
        try
        {
            string verifier = RandomUrlSafe(64);
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state = RandomUrlSafe(16);

            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();

            string url = AuthorizeUrl + "?" + Query(
                ("client_id", _clientId),
                ("response_type", "code"),
                ("redirect_uri", RedirectUri),
                ("code_challenge_method", "S256"),
                ("code_challenge", challenge),
                ("state", state),
                ("scope", Scopes));
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            // Browsers often open an idle "preconnect" socket before the real request, so every connection is
            // handled on its own; one silent socket must never block the callback.
            var result = new TaskCompletionSource<(string? code, string? error)>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var timeout = cts.Token.Register(() => result.TrySetCanceled());
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var client = await listener.AcceptTcpClientAsync(cts.Token);
                        _ = HandleClientAsync(client, state, result);
                    }
                }
                catch { /* listener stopped or cancelled */ }
            });

            Log.Write("spotify login: browser opened, waiting for the callback on " + RedirectUri);
            var (code, error) = await result.Task;
            Log.Write("spotify login: callback received (" + (code != null ? "code" : "error=" + error) + ")");

            if (code == null) return "Login failed: " + error;

            string? tokenError = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = _clientId,
                ["code_verifier"] = verifier,
            });
            if (tokenError != null)
            {
                Log.Write("spotify login: token exchange failed: " + tokenError);
                return "Login failed: " + tokenError;
            }
            Log.Write("spotify login: connected");

            ConnectionChanged?.Invoke();
            return null;
        }
        catch (OperationCanceledException)
        {
            Log.Write("spotify login: timed out waiting for the browser callback");
            return "Login timed out";
        }
        catch (SocketException) { return $"Port {Port} is in use by another program"; }
        catch (Exception ex)
        {
            Log.Write("spotify login: " + ex.Message);
            return "Login failed: " + ex.Message;
        }
        finally
        {
            listener?.Stop();
            _loggingIn = false;
        }
    }

    public void Logout()
    {
        _token = null;
        try { File.Delete(TokenPath); } catch { }
        ConnectionChanged?.Invoke();
    }

    // ---- tokens ---------------------------------------------------------------------------

    /// <summary>A valid access token, refreshing it first when it is about to expire. Null if not connected.</summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        var t = _token;
        if (t == null) return null;
        if (t.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60)) return t.AccessToken;

        await _refreshGate.WaitAsync();
        try
        {
            t = _token;
            if (t == null) return null;
            if (t.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60)) return t.AccessToken;

            string? err = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = t.RefreshToken,
                ["client_id"] = _clientId,
            });
            if (err != null)
            {
                Log.Write("spotify refresh failed: " + err);
                if (err.Contains("invalid_grant", StringComparison.Ordinal)) Logout(); // revoked or expired: sign in again
                return null;
            }
            return _token?.AccessToken;
        }
        finally { _refreshGate.Release(); }
    }

    /// <summary>Force a refresh on the next call (after a 401).</summary>
    public void Invalidate()
    {
        var t = _token;
        if (t != null) _token = t with { ExpiresAt = DateTimeOffset.MinValue };
    }

    async Task<string?> RequestTokenAsync(Dictionary<string, string> form)
    {
        try
        {
            using var res = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form));
            string text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return $"{(int)res.StatusCode} {text}";

            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            string access = r.GetProperty("access_token").GetString()!;
            string refresh = r.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } s ? s : _token?.RefreshToken ?? "";
            int expires = r.GetProperty("expires_in").GetInt32();
            _token = new TokenData(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expires));
            SaveToken();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    void LoadToken()
    {
        try
        {
            if (!File.Exists(TokenPath)) return;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
            _token = JsonSerializer.Deserialize<TokenData>(plain);
        }
        catch (Exception ex)
        {
            Log.Write("spotify token load failed: " + ex.Message);
            _token = null;
        }
    }

    void SaveToken()
    {
        try
        {
            if (_token == null) return;
            Directory.CreateDirectory(Settings.Dir);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(_token);
            File.WriteAllBytes(TokenPath, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) { Log.Write("spotify token save failed: " + ex.Message); }
    }

    // ---- helpers ----------------------------------------------------------------------------

    static string RandomUrlSafe(int length) => Base64Url(RandomNumberGenerator.GetBytes(length))[..length];

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string Query(params (string key, string value)[] pairs) =>
        string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.key)}={Uri.EscapeDataString(p.value)}"));

    static async Task HandleClientAsync(TcpClient client, string state, TaskCompletionSource<(string? code, string? error)> result)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var buf = new byte[8192];
                int n = await stream.ReadAsync(buf, readTimeout.Token);
                if (n <= 0) return; // idle preconnect socket

                string requestLine = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0]; // GET /callback?code=... HTTP/1.1
                string[] parts = requestLine.Split(' ');
                if (parts.Length < 2 || !parts[1].StartsWith("/callback", StringComparison.Ordinal))
                {
                    await RespondAsync(stream, "Not found", 404); // e.g. a favicon request
                    return;
                }

                var q = HttpUtility.ParseQueryString(new Uri("http://127.0.0.1" + parts[1]).Query);
                string? code = null, error = null;
                if (q["state"] != state) error = "state mismatch";
                else if (q["error"] != null) error = q["error"];
                else code = q["code"];

                await RespondAsync(stream, code != null
                    ? "Connected to Spotify. You can close this tab and go back to the notch."
                    : "Spotify login failed: " + error, code != null ? 200 : 400);
                result.TrySetResult((code, error));
            }
            catch (Exception ex)
            {
                Log.Write("spotify login: connection handler: " + ex.Message);
            }
        }
    }

    static async Task RespondAsync(NetworkStream stream, string message, int status)
    {
        string html = "<!doctype html><meta charset=utf-8><title>Spotify Notch</title>" +
                      "<body style=\"font-family:Segoe UI,sans-serif;background:#050507;color:#fff;display:grid;place-items:center;height:100vh;margin:0\">" +
                      $"<p style=\"font-size:18px\">{WebUtility.HtmlEncode(message)}</p></body>";
        byte[] body = Encoding.UTF8.GetBytes(html);
        string head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
    }
}
