using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexConsoleChat;

public sealed class CodexAuthManager
{
    // Matches the official Codex CLI OAuth client id.
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    private static readonly TimeSpan AccessTokenRefreshWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DeviceLoginTimeout = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly CodexOptions _options;

    public CodexAuthManager(HttpClient httpClient, CodexOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public string AuthFilePath => Path.Combine(_options.CodexHome, "auth.json");

    public async Task<bool> HasUsableCredentialsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await GetAccessTokenAsync(forceRefresh: false, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<CodexCredentials> GetAccessTokenAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        string? envToken = ReadEnvironmentToken();
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            return new CodexCredentials(
                envToken,
                _options.ChatGptAccountId,
                IsEnvironmentOverride: true,
                Source: "CODEX_ACCESS_TOKEN");
        }

        AuthDotJson auth = await LoadAuthAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                $"Not logged in. Run /login or /login-device, or set CODEX_ACCESS_TOKEN. Auth cache: {AuthFilePath}");

        if (auth.Tokens?.AccessToken is not { Length: > 0 } accessToken)
        {
            throw new InvalidOperationException(
                $"The auth cache does not contain a ChatGPT access token. Run /login again. Auth cache: {AuthFilePath}");
        }

        if (forceRefresh || IsTokenExpiringSoon(accessToken))
        {
            auth = await RefreshCachedAuthAsync(auth, cancellationToken);
            accessToken = auth.Tokens?.AccessToken
                ?? throw new InvalidOperationException("Token refresh completed but no access token was saved.");
        }

        string? accountId = _options.ChatGptAccountId
            ?? auth.Tokens?.AccountId
            ?? ParseJwtClaims(auth.Tokens?.IdToken).AccountId
            ?? ParseJwtClaims(accessToken).AccountId;

        return new CodexCredentials(
            accessToken,
            accountId,
            IsEnvironmentOverride: false,
            Source: AuthFilePath);
    }

    public Task<CodexCredentials> ForceRefreshAsync(CancellationToken cancellationToken = default)
    {
        return GetAccessTokenAsync(forceRefresh: true, cancellationToken);
    }

    public async Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        string? envToken = ReadEnvironmentToken();
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            ParsedJwtClaims claims = ParseJwtClaims(envToken);
            return new AuthStatus(
                IsAuthenticated: true,
                Source: "CODEX_ACCESS_TOKEN",
                Email: claims.Email,
                AccountId: _options.ChatGptAccountId ?? claims.AccountId,
                PlanType: claims.PlanType,
                ExpiresAt: claims.ExpiresAt,
                AuthFilePath: AuthFilePath,
                IsEnvironmentOverride: true,
                Message: "Using environment token. Local login cache and refresh are bypassed.");
        }

        AuthDotJson? auth = await LoadAuthAsync(cancellationToken);
        if (auth is null)
        {
            return new AuthStatus(
                IsAuthenticated: false,
                Source: "none",
                Email: null,
                AccountId: null,
                PlanType: null,
                ExpiresAt: null,
                AuthFilePath: AuthFilePath,
                IsEnvironmentOverride: false,
                Message: "No cached login found.");
        }

        if (auth.Tokens?.AccessToken is { Length: > 0 } accessToken)
        {
            ParsedJwtClaims idClaims = ParseJwtClaims(auth.Tokens.IdToken);
            ParsedJwtClaims accessClaims = ParseJwtClaims(accessToken);

            return new AuthStatus(
                IsAuthenticated: true,
                Source: "auth.json",
                Email: idClaims.Email ?? accessClaims.Email,
                AccountId: _options.ChatGptAccountId ?? auth.Tokens.AccountId ?? idClaims.AccountId ?? accessClaims.AccountId,
                PlanType: idClaims.PlanType ?? accessClaims.PlanType,
                ExpiresAt: accessClaims.ExpiresAt,
                AuthFilePath: AuthFilePath,
                IsEnvironmentOverride: false,
                Message: IsTokenExpiringSoon(accessToken)
                    ? "Cached access token is near expiry; it will refresh before the next request."
                    : "Cached ChatGPT login is available.");
        }

        if (!string.IsNullOrWhiteSpace(auth.OpenAiApiKey))
        {
            return new AuthStatus(
                IsAuthenticated: false,
                Source: "auth.json",
                Email: null,
                AccountId: null,
                PlanType: null,
                ExpiresAt: null,
                AuthFilePath: AuthFilePath,
                IsEnvironmentOverride: false,
                Message: "auth.json contains an API key, but this sample connects to the ChatGPT Codex backend and needs ChatGPT OAuth tokens.");
        }

        return new AuthStatus(
            IsAuthenticated: false,
            Source: "auth.json",
            Email: null,
            AccountId: null,
            PlanType: null,
            ExpiresAt: null,
            AuthFilePath: AuthFilePath,
            IsEnvironmentOverride: false,
            Message: "auth.json exists, but it does not contain a supported ChatGPT token set.");
    }

    public async Task<CodexCredentials> LoginWithBrowserAsync(CancellationToken cancellationToken = default)
    {
        PkceCodes pkce = GeneratePkceCodes();
        string state = GenerateBase64UrlRandom(32);

        TcpListener listener = BindLoginListener();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string redirectUri = $"http://localhost:{port}/auth/callback";
        string authUrl = BuildAuthorizeUrl(redirectUri, pkce.CodeChallenge, state);

        Console.WriteLine($"Starting local login server on {redirectUri}");
        Console.WriteLine("Opening browser for ChatGPT sign-in.");
        Console.WriteLine("If it did not open, paste this URL into your browser:");
        Console.WriteLine(authUrl);
        Console.WriteLine();

        TryOpenBrowser(authUrl);

        try
        {
            TokenResponse tokens = await WaitForBrowserCallbackAndExchangeAsync(
                listener,
                redirectUri,
                pkce.CodeVerifier,
                state,
                cancellationToken);

            await SaveTokensAsync(tokens, cancellationToken);
            return await GetAccessTokenAsync(forceRefresh: false, cancellationToken);
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<CodexCredentials> LoginWithDeviceCodeAsync(CancellationToken cancellationToken = default)
    {
        DeviceCode deviceCode = await RequestDeviceCodeAsync(cancellationToken);

        Console.WriteLine("Follow these steps to sign in with ChatGPT using a device code:");
        Console.WriteLine($"1. Open: {deviceCode.VerificationUrl}");
        Console.WriteLine($"2. Enter code: {deviceCode.UserCode}");
        Console.WriteLine("The code expires in 15 minutes. Never share this code with anyone.");
        Console.WriteLine();

        DeviceTokenReady tokenReady = await PollForDeviceAuthorizationAsync(deviceCode, cancellationToken);
        string redirectUri = $"{_options.AuthIssuer.TrimEnd('/')}/deviceauth/callback";
        TokenResponse tokens = await ExchangeCodeForTokensAsync(
            tokenReady.AuthorizationCode,
            redirectUri,
            tokenReady.CodeVerifier,
            cancellationToken);

        await SaveTokensAsync(tokens, cancellationToken);
        return await GetAccessTokenAsync(forceRefresh: false, cancellationToken);
    }

    public async Task LogoutAsync(bool revoke = true, CancellationToken cancellationToken = default)
    {
        AuthDotJson? auth = await LoadAuthAsync(cancellationToken);

        if (revoke && auth?.Tokens is { } tokens)
        {
            string? tokenToRevoke = !string.IsNullOrWhiteSpace(tokens.RefreshToken)
                ? tokens.RefreshToken
                : tokens.AccessToken;

            string tokenTypeHint = !string.IsNullOrWhiteSpace(tokens.RefreshToken)
                ? "refresh_token"
                : "access_token";

            if (!string.IsNullOrWhiteSpace(tokenToRevoke))
            {
                try
                {
                    await RevokeTokenAsync(tokenToRevoke, tokenTypeHint, cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: token revoke failed, deleting local cache anyway: {ex.Message}");
                }
            }
        }

        if (File.Exists(AuthFilePath))
        {
            File.Delete(AuthFilePath);
        }
    }

    private async Task<TokenResponse> WaitForBrowserCallbackAndExchangeAsync(
        TcpListener listener,
        string redirectUri,
        string codeVerifier,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
            NetworkStream stream = client.GetStream();
            string? target = await ReadHttpRequestTargetAsync(stream, cancellationToken);

            if (string.IsNullOrWhiteSpace(target))
            {
                await SendHtmlAsync(stream, 400, "Bad Request", "<h1>Bad request</h1>", cancellationToken);
                continue;
            }

            Uri requestUri = BuildLocalRequestUri(target);
            if (requestUri.AbsolutePath.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
            {
                await SendHtmlAsync(stream, 200, "OK", "<h1>Login cancelled</h1>", cancellationToken);
                throw new OperationCanceledException("Login cancelled by browser callback.", cancellationToken);
            }

            if (!requestUri.AbsolutePath.Equals("/auth/callback", StringComparison.OrdinalIgnoreCase))
            {
                await SendHtmlAsync(stream, 404, "Not Found", "<h1>Not found</h1>", cancellationToken);
                continue;
            }

            Dictionary<string, string> query = ParseQuery(requestUri.Query);
            if (!query.TryGetValue("state", out string? state) || state != expectedState)
            {
                await SendHtmlAsync(stream, 400, "Bad Request", "<h1>State mismatch</h1>", cancellationToken);
                throw new InvalidOperationException("OAuth state mismatch. Login aborted.");
            }

            if (query.TryGetValue("error", out string? error))
            {
                query.TryGetValue("error_description", out string? errorDescription);
                string body = $"<h1>Codex login failed</h1><p>{WebUtility.HtmlEncode(errorDescription ?? error)}</p>";
                await SendHtmlAsync(stream, 403, "Forbidden", body, cancellationToken);
                throw new InvalidOperationException($"OAuth login failed: {errorDescription ?? error}");
            }

            if (!query.TryGetValue("code", out string? code) || string.IsNullOrWhiteSpace(code))
            {
                await SendHtmlAsync(stream, 400, "Bad Request", "<h1>Missing authorization code</h1>", cancellationToken);
                throw new InvalidOperationException("OAuth callback did not include an authorization code.");
            }

            try
            {
                TokenResponse tokens = await ExchangeCodeForTokensAsync(code, redirectUri, codeVerifier, cancellationToken);
                await SendHtmlAsync(
                    stream,
                    200,
                    "OK",
                    "<h1>Codex login complete</h1><p>You can close this browser tab and return to the console.</p>",
                    cancellationToken);
                return tokens;
            }
            catch (Exception ex)
            {
                string body = $"<h1>Codex token exchange failed</h1><p>{WebUtility.HtmlEncode(ex.Message)}</p>";
                await SendHtmlAsync(stream, 500, "Internal Server Error", body, cancellationToken);
                throw;
            }
        }
    }

    private async Task<TokenResponse> ExchangeCodeForTokensAsync(
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        string endpoint = $"{_options.AuthIssuer.TrimEnd('/')}/oauth/token";
        using FormUrlEncodedContent content = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = codeVerifier
        });

        using HttpResponseMessage response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"OAuth token exchange failed with HTTP {(int)response.StatusCode}: {body}");
        }

        TokenResponse? tokenResponse = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions);
        if (tokenResponse?.AccessToken is not { Length: > 0 }
            || tokenResponse.IdToken is not { Length: > 0 }
            || tokenResponse.RefreshToken is not { Length: > 0 })
        {
            throw new InvalidOperationException("OAuth token exchange response did not include id_token, access_token, and refresh_token.");
        }

        return tokenResponse;
    }

    private async Task<DeviceCode> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        string endpoint = $"{_options.AuthIssuer.TrimEnd('/')}/api/accounts/deviceauth/usercode";
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        request.Content = JsonContent(new Dictionary<string, string> { ["client_id"] = ClientId });

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException("Device-code login is not enabled for this Codex server. Use /login for browser login.");
            }

            throw new HttpRequestException($"Device-code request failed with HTTP {(int)response.StatusCode}: {body}");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string deviceAuthId = RequiredString(root, "device_auth_id");
        string userCode = TryGetString(root, "user_code") ?? RequiredString(root, "usercode");
        int interval = TryGetInt(root, "interval") ?? 5;

        return new DeviceCode(
            VerificationUrl: $"{_options.AuthIssuer.TrimEnd('/')}/codex/device",
            UserCode: userCode,
            DeviceAuthId: deviceAuthId,
            Interval: Math.Max(1, interval));
    }

    private async Task<DeviceTokenReady> PollForDeviceAuthorizationAsync(
        DeviceCode deviceCode,
        CancellationToken cancellationToken)
    {
        string endpoint = $"{_options.AuthIssuer.TrimEnd('/')}/api/accounts/deviceauth/token";
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(DeviceLoginTimeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
            request.Content = JsonContent(new Dictionary<string, string>
            {
                ["device_auth_id"] = deviceCode.DeviceAuthId,
                ["user_code"] = deviceCode.UserCode
            });

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                return new DeviceTokenReady(
                    AuthorizationCode: RequiredString(root, "authorization_code"),
                    CodeVerifier: RequiredString(root, "code_verifier"));
            }

            if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound))
            {
                throw new HttpRequestException($"Device authorization failed with HTTP {(int)response.StatusCode}: {body}");
            }

            TimeSpan delay = TimeSpan.FromSeconds(deviceCode.Interval);
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(delay < remaining ? delay : remaining, cancellationToken);
        }

        throw new TimeoutException("Device-code login timed out after 15 minutes.");
    }

    private async Task<AuthDotJson> RefreshCachedAuthAsync(AuthDotJson auth, CancellationToken cancellationToken)
    {
        if (auth.Tokens?.RefreshToken is not { Length: > 0 } refreshToken)
        {
            throw new InvalidOperationException("Cannot refresh because the cached login does not contain a refresh token. Run /login again.");
        }

        string endpoint = Environment.GetEnvironmentVariable("CODEX_REFRESH_TOKEN_URL_OVERRIDE")
            ?? $"{_options.AuthIssuer.TrimEnd('/')}/oauth/token";

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        request.Content = JsonContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Token refresh failed with HTTP {(int)response.StatusCode}: {body}");
        }

        TokenResponse? refreshResponse = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions);
        if (refreshResponse is null)
        {
            throw new InvalidOperationException("Token refresh response could not be parsed.");
        }

        auth.Tokens.IdToken = refreshResponse.IdToken ?? auth.Tokens.IdToken;
        auth.Tokens.AccessToken = refreshResponse.AccessToken ?? auth.Tokens.AccessToken;
        auth.Tokens.RefreshToken = refreshResponse.RefreshToken ?? auth.Tokens.RefreshToken;
        auth.Tokens.AccountId = ParseJwtClaims(auth.Tokens.IdToken).AccountId
            ?? ParseJwtClaims(auth.Tokens.AccessToken).AccountId
            ?? auth.Tokens.AccountId;
        auth.LastRefresh = DateTimeOffset.UtcNow;

        await SaveAuthAsync(auth, cancellationToken);
        return auth;
    }

    private async Task SaveTokensAsync(TokenResponse tokens, CancellationToken cancellationToken)
    {
        AuthDotJson auth = new()
        {
            AuthMode = "chatgpt",
            Tokens = new TokenData
            {
                IdToken = tokens.IdToken,
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                AccountId = ParseJwtClaims(tokens.IdToken).AccountId ?? ParseJwtClaims(tokens.AccessToken).AccountId
            },
            LastRefresh = DateTimeOffset.UtcNow
        };

        await SaveAuthAsync(auth, cancellationToken);
    }

    private async Task SaveAuthAsync(AuthDotJson auth, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.CodexHome);
        string json = JsonSerializer.Serialize(auth, JsonOptions);
        await File.WriteAllTextAsync(AuthFilePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        TryRestrictFilePermissions(AuthFilePath);
    }

    private async Task<AuthDotJson?> LoadAuthAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(AuthFilePath))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(AuthFilePath, cancellationToken);
        return JsonSerializer.Deserialize<AuthDotJson>(json, JsonOptions);
    }

    private async Task RevokeTokenAsync(string token, string tokenTypeHint, CancellationToken cancellationToken)
    {
        string endpoint = Environment.GetEnvironmentVariable("CODEX_REVOKE_TOKEN_URL_OVERRIDE")
            ?? $"{_options.AuthIssuer.TrimEnd('/')}/oauth/revoke";

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        Dictionary<string, string> body = new()
        {
            ["token"] = token,
            ["token_type_hint"] = tokenTypeHint
        };

        if (tokenTypeHint == "refresh_token")
        {
            body["client_id"] = ClientId;
        }

        request.Content = JsonContent(body);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Token revoke failed with HTTP {(int)response.StatusCode}: {responseBody}");
        }
    }

    private string BuildAuthorizeUrl(string redirectUri, string codeChallenge, string state)
    {
        Dictionary<string, string> query = new()
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid profile email offline_access api.connectors.read api.connectors.invoke",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = state,
            ["originator"] = _options.Originator
        };

        return $"{_options.AuthIssuer.TrimEnd('/')}/oauth/authorize?" + string.Join(
            "&",
            query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private TcpListener BindLoginListener()
    {
        List<int> ports = [_options.LoginPort];
        if (_options.LoginPort != 1457)
        {
            ports.Add(1457);
        }

        Exception? lastError = null;
        foreach (int port in ports.Distinct())
        {
            try
            {
                TcpListener listener = new(IPAddress.Loopback, port);
                listener.Start();
                return listener;
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("Could not bind a local OAuth callback server on ports 1455 or 1457.", lastError);
    }

    private static async Task<string?> ReadHttpRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        string? firstLine = await reader.ReadLineAsync(cancellationToken);
        if (firstLine is null)
        {
            return null;
        }

        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || line.Length == 0)
            {
                break;
            }
        }

        string[] parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static async Task SendHtmlAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        string html,
        CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes("<!doctype html><html><body>" + html + "</body></html>");
        string header =
            $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static Uri BuildLocalRequestUri(string target)
    {
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(target);
        }

        if (!target.StartsWith('/'))
        {
            target = "/" + target;
        }

        return new Uri("http://localhost" + target);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (query.StartsWith('?'))
        {
            query = query[1..];
        }

        if (query.Length == 0)
        {
            return result;
        }

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equalsIndex = pair.IndexOf('=');
            string key = equalsIndex >= 0 ? pair[..equalsIndex] : pair;
            string value = equalsIndex >= 0 ? pair[(equalsIndex + 1)..] : string.Empty;
            key = Uri.UnescapeDataString(key.Replace('+', ' '));
            value = Uri.UnescapeDataString(value.Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }

    private static HttpContent JsonContent(object value)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        StringContent content = new(json, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static bool IsTokenExpiringSoon(string jwt)
    {
        DateTimeOffset? expiresAt = ParseJwtClaims(jwt).ExpiresAt;
        return expiresAt.HasValue && expiresAt.Value <= DateTimeOffset.UtcNow.Add(AccessTokenRefreshWindow);
    }

    private static ParsedJwtClaims ParseJwtClaims(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return ParsedJwtClaims.Empty;
        }

        try
        {
            string[] parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                return ParsedJwtClaims.Empty;
            }

            byte[] payloadBytes = Base64UrlDecode(parts[1]);
            using JsonDocument document = JsonDocument.Parse(payloadBytes);
            JsonElement root = document.RootElement;

            string? email = TryGetString(root, "email");
            if (email is null
                && root.TryGetProperty("https://api.openai.com/profile", out JsonElement profile))
            {
                email = TryGetString(profile, "email");
            }

            string? accountId = null;
            string? planType = null;
            if (root.TryGetProperty("https://api.openai.com/auth", out JsonElement auth))
            {
                accountId = TryGetString(auth, "chatgpt_account_id");
                planType = TryGetString(auth, "chatgpt_plan_type");
            }

            DateTimeOffset? expiresAt = null;
            if (root.TryGetProperty("exp", out JsonElement exp) && exp.ValueKind == JsonValueKind.Number)
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
            }

            return new ParsedJwtClaims(email, accountId, planType, expiresAt);
        }
        catch
        {
            return ParsedJwtClaims.Empty;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid base64url length.")
        };

        return Convert.FromBase64String(padded);
    }

    private static PkceCodes GeneratePkceCodes()
    {
        string verifier = GenerateBase64UrlRandom(64);
        byte[] challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        string challenge = Base64UrlEncode(challengeBytes);
        return new PkceCodes(verifier, challenge);
    }

    private static string GenerateBase64UrlRandom(int byteCount)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        return TryGetString(element, propertyName)
            ?? throw new InvalidOperationException($"Response JSON did not include '{propertyName}'.");
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed))
        {
            return parsed;
        }

        return null;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch
        {
            // The URL is printed, so a failed browser launch is not fatal.
        }
    }

    private static void TryRestrictFilePermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort only. Some filesystems do not support Unix mode changes.
        }
    }

    private static string? ReadEnvironmentToken()
    {
        string? value = Environment.GetEnvironmentVariable("CODEX_ACCESS_TOKEN");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class AuthDotJson
    {
        [JsonPropertyName("auth_mode")]
        public string? AuthMode { get; set; }

        [JsonPropertyName("OPENAI_API_KEY")]
        public string? OpenAiApiKey { get; set; }

        [JsonPropertyName("tokens")]
        public TokenData? Tokens { get; set; }

        [JsonPropertyName("last_refresh")]
        public DateTimeOffset? LastRefresh { get; set; }

        [JsonPropertyName("agent_identity")]
        public string? AgentIdentity { get; set; }
    }

    private sealed class TokenData
    {
        [JsonPropertyName("id_token")]
        public string? IdToken { get; set; }

        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("account_id")]
        public string? AccountId { get; set; }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("id_token")]
        public string? IdToken { get; set; }

        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }
    }

    private sealed record PkceCodes(string CodeVerifier, string CodeChallenge);

    private sealed record DeviceCode(
        string VerificationUrl,
        string UserCode,
        string DeviceAuthId,
        int Interval);

    private sealed record DeviceTokenReady(string AuthorizationCode, string CodeVerifier);

    private sealed record ParsedJwtClaims(
        string? Email,
        string? AccountId,
        string? PlanType,
        DateTimeOffset? ExpiresAt)
    {
        public static ParsedJwtClaims Empty { get; } = new(null, null, null, null);
    }
}
