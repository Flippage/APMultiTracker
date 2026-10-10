using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace APMultiTracker;

/// <summary>
/// Optional Twitch sign-in (device code, public client, no scopes) used only to search categories for box art.
/// Without it, box art still loads from Twitch's public CDN by game name.
/// </summary>
public sealed class TwitchService
{
    private const string TokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string Helix = "https://api.twitch.tv/helix/";
    private const string Scopes = "";

    /// <summary>Public client (no secret). Shared with AutoSwitcher; register your own on dev.twitch.tv to change it.</summary>
    public const string ClientId = "q27xh0as8wnpw5ei4ckwgqzxlgjakm";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public TokenSet? Tokens { get; private set; }
    public bool IsSignedIn => Tokens != null;
    public event Action? AuthChanged;

    public TwitchService() => Tokens = ConfigStore.LoadTokens();

    public async Task InitAsync()
    {
        if (Tokens == null) return;
        await RefreshAsync(force: true);
        if (Tokens != null) await LoadUserAsync();
        AuthChanged?.Invoke();
    }

    public async Task<DeviceCode> StartDeviceFlowAsync()
    {
        using var resp = await Http.PostAsync("https://id.twitch.tv/oauth2/device",
            Form(("client_id", ClientId), ("scopes", Scopes)));
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(ErrorMessage(body));
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        return new DeviceCode(
            r.GetProperty("device_code").GetString()!,
            r.GetProperty("user_code").GetString()!,
            r.GetProperty("verification_uri").GetString()!,
            r.GetProperty("expires_in").GetInt32(),
            r.TryGetProperty("interval", out var i) ? i.GetInt32() : 5);
    }

    public async Task<bool> CompleteDeviceFlowAsync(DeviceCode dc, CancellationToken ct)
    {
        int interval = Math.Max(1, dc.Interval);
        DateTime deadline = DateTime.UtcNow.AddSeconds(dc.ExpiresIn);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            using var resp = await Http.PostAsync(TokenUrl, Form(
                ("client_id", ClientId),
                ("scopes", Scopes),
                ("device_code", dc.Code),
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code")), ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                SetTokens(ParseToken(body, null));
                await LoadUserAsync();
                AuthChanged?.Invoke();
                return true;
            }
            string msg = ErrorMessage(body);
            if (msg.Contains("pending", StringComparison.OrdinalIgnoreCase)) continue;
            if (msg.Contains("slow_down", StringComparison.OrdinalIgnoreCase)) { interval += 5; continue; }
            if (msg.Contains("invalid device code", StringComparison.OrdinalIgnoreCase)) return false;
            throw new InvalidOperationException(msg);
        }
        return false;
    }

    public async Task SignOutAsync()
    {
        var t = Tokens;
        Tokens = null;
        ConfigStore.SaveTokens(null);
        AuthChanged?.Invoke();
        if (t == null) return;
        try
        {
            using var _ = await Http.PostAsync("https://id.twitch.tv/oauth2/revoke",
                Form(("client_id", ClientId), ("token", t.AccessToken)));
        }
        catch { }
    }

    private async Task<bool> RefreshAsync(bool force = false)
    {
        var before = Tokens;
        if (before == null) return false;
        await _refreshLock.WaitAsync();
        try
        {
            if (!ReferenceEquals(before, Tokens)) return Tokens != null;
            if (!force && Tokens!.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2)) return true;
            using var resp = await Http.PostAsync(TokenUrl, Form(
                ("client_id", ClientId),
                ("grant_type", "refresh_token"),
                ("refresh_token", before.RefreshToken)));
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                {
                    Tokens = null;
                    ConfigStore.SaveTokens(null);
                    AuthChanged?.Invoke();
                }
                return false;
            }
            SetTokens(ParseToken(body, before));
            return true;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
        finally { _refreshLock.Release(); }
    }

    private async Task LoadUserAsync()
    {
        try
        {
            using var resp = await ApiAsync("users");
            if (resp == null || !resp.IsSuccessStatusCode || Tokens == null) return;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var u = doc.RootElement.GetProperty("data")[0];
            Tokens.Login = u.GetProperty("login").GetString() ?? "";
            Tokens.DisplayName = u.GetProperty("display_name").GetString() ?? Tokens.Login;
            Tokens.ProfileImageUrl = u.GetProperty("profile_image_url").GetString() ?? "";
            ConfigStore.SaveTokens(Tokens);
        }
        catch { }
    }

    private void SetTokens(TokenSet t)
    {
        Tokens = t;
        ConfigStore.SaveTokens(t);
    }

    private static TokenSet ParseToken(string body, TokenSet? previous)
    {
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        return new TokenSet
        {
            AccessToken = r.GetProperty("access_token").GetString()!,
            RefreshToken = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : previous?.RefreshToken ?? "",
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(r.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600),
            Login = previous?.Login ?? "",
            DisplayName = previous?.DisplayName ?? "",
            ProfileImageUrl = previous?.ProfileImageUrl ?? "",
        };
    }

    public async Task<List<CategoryRef>> SearchCategoriesAsync(string query, CancellationToken ct)
    {
        var list = new List<CategoryRef>();
        using var resp = await ApiAsync("search/categories?first=20&query=" + Uri.EscapeDataString(query), ct);
        if (resp == null || !resp.IsSuccessStatusCode) return list;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        foreach (var c in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            list.Add(new CategoryRef
            {
                Id = c.GetProperty("id").GetString() ?? "",
                Name = c.GetProperty("name").GetString() ?? "",
                BoxArtUrl = c.GetProperty("box_art_url").GetString() ?? "",
            });
        }
        return list;
    }

    private async Task<HttpResponseMessage?> ApiAsync(string path, CancellationToken ct = default)
    {
        if (Tokens == null) return null;
        if (Tokens.ExpiresAtUtc < DateTime.UtcNow.AddMinutes(1)) await RefreshAsync();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var t = Tokens;
            if (t == null) return null;
            using var req = new HttpRequestMessage(HttpMethod.Get, Helix + path);
            req.Headers.Add("Client-Id", ClientId);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.AccessToken);
            var resp = await Http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                resp.Dispose();
                if (!await RefreshAsync(force: true)) return null;
                continue;
            }
            return resp;
        }
        return null;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] pairs)
    {
        var list = new List<KeyValuePair<string, string>>(pairs.Length);
        foreach (var (k, v) in pairs) list.Add(new KeyValuePair<string, string>(k, v));
        return new FormUrlEncodedContent(list);
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString() ?? body;
        }
        catch { }
        return string.IsNullOrWhiteSpace(body) ? "Unknown Twitch error" : body;
    }
}
