using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;
using RandomNumberGenerator = System.Security.Cryptography.RandomNumberGenerator;

namespace TierListOverlay;

/// <summary>
/// The player's Untapped.gg login, which the stats API needs. Logging in happens in the
/// browser (OAuth with PKCE, redirected back to a local port). The tokens are kept in
/// account.cfg next to the mod DLL and renewed as they expire. Without a login everything else
/// works as before.
/// </summary>
internal static class Account
{
    public enum State
    {
        NotLoggedIn,
        // Checking the saved login at game start.
        Checking,
        LoggingIn,
        Connected,
        // The saved login no longer works. Logging in again fixes it.
        Expired,
        // Untapped.gg could not be reached or had trouble, but the login may still be fine.
        // Checked again every minute.
        Offline,
    }

    public static State Status { get; private set; } = State.NotLoggedIn;

    /// <summary>Why the last login or request failed, for the settings screen.</summary>
    public static string Problem { get; private set; } = "";

    public static bool Usable => Status is State.Connected or State.Checking;

    private const string ClientId = "Iin3tp4hpdfUT0DZT0EXX6S0CDPSEqJi1Rl9R0Oc";
    private const string Scope = "companion:full-access";
    private const string AuthorizeUrl = "https://accounts.untapped.gg/authorize";
    private const string AccountApi = "https://api.accounts.untapped.gg";
    private const string AccountTokenUrl = AccountApi + "/o/token";
    private const string GameApi = "https://api.sts2.untapped.gg";
    private const string SuccessPage = "https://sts2.untapped.gg/uc/oauth-success";
    private const string FailurePage = "https://accounts.untapped.gg/uc/oauth_failure/";
    private const int FirstPort = 17791, LastPort = 17799;
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryOfflineAfter = TimeSpan.FromMinutes(1);

    private const string Section = "account";

    public static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var version = typeof(Account).Assembly.GetName().Version;
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"TierListOverlay/{version?.ToString(3) ?? "1.0.0"}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    // Two token pairs: the account's own, which only serves to get the game API's pair, and
    // the game API's, which stats requests use. Keeping the first means an expired game pair
    // can be replaced without logging in again.
    private sealed class Tokens
    {
        public string Access = "", Refresh = "", AccountAccess = "", AccountRefresh = "";
        // Whose login it is, as Untapped.gg names the account.
        public string Email = "";
    }

    // How a call to Untapped.gg's token endpoints went. Refused: the server no longer accepts
    // the token (the login has expired). Failed: anything else (unreachable, server trouble).
    private enum Outcome
    {
        Ok,
        Refused,
        Failed,
    }

    /// <summary>The logged-in account's email, if known.</summary>
    public static string Email => _tokens?.Email ?? "";

    private static string _path = "";
    // The current login. Work started for an earlier one (before a logout) checks against it
    // before it saves or reports anything.
    private static Tokens? _tokens;
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static Login? _login;
    private static DateTime _nextOfflineCheck;
    private static bool _checking;
    // Set by the player's own login, which finishes in the background, for the next frame.
    private static volatile bool _loggedIn;

    private sealed class Login
    {
        public readonly CancellationTokenSource Cancel = new();
        public bool CancelledByPlayer;
    }

    /// <summary>Reads the saved login and checks in the background that it still works.</summary>
    public static void Load(string path)
    {
        _path = path;
        var config = new ConfigFile();
        if (config.Load(path) != Error.Ok)
            return;
        var tokens = new Tokens
        {
            Access = config.GetValue(Section, "access_token", "").AsString(),
            Refresh = config.GetValue(Section, "refresh_token", "").AsString(),
            AccountAccess = config.GetValue(Section, "global_access_token", "").AsString(),
            AccountRefresh = config.GetValue(Section, "global_refresh_token", "").AsString(),
            Email = config.GetValue(Section, "email", "").AsString(),
        };
        if (tokens.Refresh == "" && tokens.AccountRefresh == "")
            return;
        _tokens = tokens;
        Status = State.Checking;
        // The game token lasts hours. Renewing it once per game start shows whether the login
        // still works before the first stats are needed.
        Check(tokens);
    }

    /// <summary>
    /// Checks an unreachable login again now and then, so stats come back. After the player's
    /// own login, switches the badges to other players' stats.
    /// </summary>
    public static void OnProcessFrame()
    {
        if (_loggedIn)
        {
            _loggedIn = false;
            Settings.OnLoggedIn();
        }
        if (Status == State.Offline && _tokens is { } tokens && !_checking && DateTime.UtcNow >= _nextOfflineCheck)
            Check(tokens);
    }

    private static void Check(Tokens tokens)
    {
        _checking = true;
        _nextOfflineCheck = DateTime.UtcNow + RetryOfflineAfter;
        Task.Run(async () =>
        {
            await Lock.WaitAsync();
            try
            {
                if (await Renew(tokens))
                {
                    SetStatus(tokens, State.Connected, "");
                    if (tokens.Email == "")
                        await FetchEmail(tokens);
                }
            }
            finally
            {
                Lock.Release();
                _checking = false;
            }
        });
    }

    // Saves the tokens if they are still the current login (not logged out meanwhile).
    private static void Save(Tokens tokens)
    {
        if (tokens != _tokens)
            return;
        var config = new ConfigFile();
        config.SetValue(Section, "access_token", tokens.Access);
        config.SetValue(Section, "refresh_token", tokens.Refresh);
        config.SetValue(Section, "global_access_token", tokens.AccountAccess);
        config.SetValue(Section, "global_refresh_token", tokens.AccountRefresh);
        config.SetValue(Section, "email", tokens.Email);
        var err = config.Save(_path);
        if (err != Error.Ok)
            GD.PrintErr($"[TierListOverlay] Saving {_path} failed: {err}");
    }

    // Reports on a login, unless it is no longer the current one.
    private static void SetStatus(Tokens tokens, State status, string problem)
    {
        if (tokens == _tokens)
            SetStatus(status, problem);
    }

    private static void SetStatus(State status, string problem)
    {
        if (Status != status)
            GD.Print($"[TierListOverlay] Account: {status}{(problem == "" ? "" : $" ({problem})")}");
        Status = status;
        Problem = problem;
    }

    /// <summary>
    /// POSTs `body` as JSON to the game API's `path` with the login, renewing the token once if
    /// the API refuses it. Null without a working login or when the API is unreachable.
    /// </summary>
    public static async Task<string?> Post(string path, string body)
    {
        var tokens = _tokens;
        if (tokens == null || !Usable)
            return null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var access = tokens.Access;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, GameApi + path)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                using var response = await Http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    await Lock.WaitAsync();
                    try
                    {
                        // Another request may have renewed it meanwhile.
                        if (tokens.Access == access && !await Renew(tokens))
                            return null;
                    }
                    finally
                    {
                        Lock.Release();
                    }
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    GD.Print($"[TierListOverlay] {path}: HTTP {(int)response.StatusCode} {Shorten(text)}");
                    return null;
                }
                if (Status != State.Connected)
                    SetStatus(tokens, State.Connected, "");
                return text;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                SetStatus(tokens, State.Offline, ex.Message);
                return null;
            }
        }
        return null;
    }

    // Renews the game token with its refresh token, or failing that gets a new pair with the
    // account token. False (with the status set) when neither works. Call under Lock.
    private static async Task<bool> Renew(Tokens tokens)
    {
        try
        {
            var outcome = Outcome.Refused;
            if (tokens.Refresh != "")
            {
                var (renewed, json) = await RefreshAt(GameApi + "/o/token", tokens.Refresh);
                outcome = renewed;
                if (renewed == Outcome.Ok)
                {
                    tokens.Access = OptionalString(json!.Value, "access_token")!;
                    tokens.Refresh = OptionalString(json.Value, "refresh_token") ?? tokens.Refresh;
                    Save(tokens);
                    return true;
                }
            }
            // A refused game token can still be replaced with the account token.
            if (outcome == Outcome.Refused)
                outcome = await Exchange(tokens);
            if (outcome == Outcome.Ok)
                return true;
            if (outcome == Outcome.Refused)
                SetStatus(tokens, State.Expired, "Untapped.gg no longer accepts the saved login");
            else
                SetStatus(tokens, State.Offline, "Untapped.gg did not answer as expected");
            return false;
        }
        catch (Exception ex)
        {
            GD.Print($"[TierListOverlay] Renewing the login failed: {ex.Message}");
            SetStatus(tokens, State.Offline, ex.Message);
            return false;
        }
    }

    // Exchanges the account token for a game token pair, renewing the account token first if
    // needed.
    private static async Task<Outcome> Exchange(Tokens tokens)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (tokens.AccountAccess == "")
                return Outcome.Refused;
            var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["access_token"] = tokens.AccountAccess, ["client_id"] = ClientId });
            using var request = new HttpRequestMessage(HttpMethod.Post, GameApi + "/api/v1/oauth/tokens/exchange")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccountAccess);
            using var response = await Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(text);
                if (OptionalString(doc.RootElement, "access_token") is not { } access)
                    return Outcome.Failed;
                tokens.Access = access;
                tokens.Refresh = OptionalString(doc.RootElement, "refresh_token") ?? "";
                Save(tokens);
                return Outcome.Ok;
            }
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0 || tokens.AccountRefresh == "")
            {
                GD.Print($"[TierListOverlay] Token exchange: HTTP {(int)response.StatusCode} {Shorten(text)}");
                return Refusal(response.StatusCode);
            }
            if (await RenewAccountToken(tokens) is var renewed && renewed != Outcome.Ok)
                return renewed;
        }
        return Outcome.Failed;
    }

    private static async Task<Outcome> RenewAccountToken(Tokens tokens)
    {
        var (outcome, json) = await RefreshAt(AccountTokenUrl, tokens.AccountRefresh);
        if (outcome != Outcome.Ok)
            return outcome;
        tokens.AccountAccess = OptionalString(json!.Value, "access_token")!;
        tokens.AccountRefresh = OptionalString(json.Value, "refresh_token") ?? tokens.AccountRefresh;
        Save(tokens);
        return Outcome.Ok;
    }

    // Asks the accounts API whose login this is, renewing the account token first if needed.
    // The email only labels the login in the settings, so a failure just leaves it out.
    private static async Task FetchEmail(Tokens tokens)
    {
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, AccountApi + "/api/v1/account");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccountAccess);
                using var response = await Http.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && tokens.AccountRefresh != "")
                {
                    if (await RenewAccountToken(tokens) != Outcome.Ok)
                        return;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    return;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                tokens.Email = OptionalString(doc.RootElement, "email") ?? "";
                Save(tokens);
                return;
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[TierListOverlay] Looking up the account failed: {ex.Message}");
        }
    }

    // A refresh token grant, with the answer's JSON when it went through (and has a token).
    private static async Task<(Outcome Outcome, JsonElement? Json)> RefreshAt(string url, string refreshToken)
    {
        using var response = await Http.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }));
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            GD.Print($"[TierListOverlay] Renewing a token at {url}: HTTP {(int)response.StatusCode} {Shorten(text)}");
            return (Refusal(response.StatusCode), null);
        }
        var json = JsonDocument.Parse(text).RootElement.Clone();
        return OptionalString(json, "access_token") == null ? (Outcome.Failed, null) : (Outcome.Ok, json);
    }

    // The token endpoints answer 400 (invalid_grant) or 401 for a token they no longer accept.
    private static Outcome Refusal(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized ? Outcome.Refused : Outcome.Failed;

    /// <summary>
    /// Opens Untapped.gg's login page in the browser and waits (up to five minutes) for it to
    /// send the browser back to a local port with the login.
    /// </summary>
    public static void LogIn()
    {
        if (Status == State.LoggingIn)
            return;
        var login = _login = new Login();
        login.Cancel.CancelAfter(LoginTimeout);
        var before = (Status, Problem);
        SetStatus(State.LoggingIn, "");
        Task.Run(async () =>
        {
            try
            {
                var tokens = await RunLogin(login.Cancel.Token);
                // Cancelled once the browser had sent the login back.
                login.Cancel.Token.ThrowIfCancellationRequested();
                await Lock.WaitAsync();
                try
                {
                    _tokens = tokens;
                    Save(tokens);
                }
                finally
                {
                    Lock.Release();
                }
                SetStatus(State.Connected, "");
                Stats.Clear();
                _loggedIn = true;
            }
            catch (Exception) when (login != _login)
            {
                // Logged out meanwhile.
            }
            catch (Exception ex)
            {
                var cancelled = ex is OperationCanceledException && login.Cancel.IsCancellationRequested;
                var problem = !cancelled ? (ex is TaskCanceledException ? "Untapped.gg did not answer" : ex.Message)
                    : login.CancelledByPlayer ? "" : "the login timed out";
                GD.Print($"[TierListOverlay] Login {(problem == "" ? "cancelled" : $"failed: {problem}")}");
                // Back to how the login stood before, with the reason it did not work.
                SetStatus(before.Status, problem == "" ? before.Problem : problem);
            }
            finally
            {
                login.Cancel.Dispose();
            }
        });
    }

    /// <summary>Stops waiting for the browser. The login stays as it was before.</summary>
    public static void CancelLogIn()
    {
        if (Status != State.LoggingIn || _login is not { } login)
            return;
        login.CancelledByPlayer = true;
        login.Cancel.Cancel();
    }

    public static void LogOut()
    {
        var login = _login;
        _login = null;
        if (login != null && Status == State.LoggingIn)
            login.Cancel.Cancel();
        _tokens = null;
        if (File.Exists(_path))
            File.Delete(_path);
        SetStatus(State.NotLoggedIn, "");
        Stats.Clear();
    }

    private static async Task<Tokens> RunLogin(CancellationToken cancel)
    {
        using var listener = Listen(out var redirect);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var url = AuthorizeUrl + "?" + string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirect,
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge,
            ["franchise"] = "STS2",
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        Callable.From(() => OS.ShellOpen(url)).CallDeferred();

        string code;
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancel);
            var query = context.Request.QueryString;
            // Only the login page's answer counts, recognised by the state it was sent. Anything
            // else (the browser asking for a favicon, another page calling the port) is turned
            // away, and the login keeps waiting.
            if (query["state"] != state)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }
            context.Response.Redirect(query["code"] != null ? SuccessPage : FailurePage);
            context.Response.Close();
            code = query["code"] ?? throw new InvalidOperationException(query["error"] ?? "no login in the answer");
            break;
        }

        using var response = await Http.PostAsync(AccountTokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirect,
            ["scope"] = Scope,
        }), cancel);
        var text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {Shorten(text)}");
        using var doc = JsonDocument.Parse(text);
        var tokens = new Tokens
        {
            AccountAccess = OptionalString(doc.RootElement, "access_token") ?? throw new InvalidOperationException("no token in the answer"),
            AccountRefresh = OptionalString(doc.RootElement, "refresh_token") ?? "",
        };
        if (await Exchange(tokens) != Outcome.Ok)
            throw new InvalidOperationException("Untapped.gg did not hand out a game token");
        await FetchEmail(tokens);
        return tokens;
    }

    // Listens on the first free port of the range the login page accepts redirects to.
    private static HttpListener Listen(out string redirect)
    {
        for (var port = FirstPort; port <= LastPort; port++)
        {
            var listener = new HttpListener();
            redirect = $"http://localhost:{port}/";
            listener.Prefixes.Add(redirect);
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }
        throw new InvalidOperationException($"no free port between {FirstPort} and {LastPort}");
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? OptionalString(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && v.GetString() is { Length: > 0 } s ? s : null;

    private static string Shorten(string text) => text.Length > 200 ? text[..200] + "…" : text;
}
