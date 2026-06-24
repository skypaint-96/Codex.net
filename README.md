# CodexDotNet

Portable .NET 10 class library for authenticating with and calling the ChatGPT Codex backend. The project can be packed as a NuGet package and consumed from .NET 10-compatible console apps, desktop apps, services, workers, or tests.

The library provides:

- `CodexClient` / `ICodexClient` for streaming and non-streaming chat calls.
- `CodexAgent` for Microsoft Agent Framework custom-agent provider integration.
- `CodexAgentFrameworkChatClient` as a legacy Microsoft Agent Framework / `Microsoft.Extensions.AI` chat-client adapter.
- `CodexAuthManager` / `ICodexAuthManager` for ChatGPT OAuth token cache management.
- Library-friendly auth callbacks via `CodexAuthCallbacks`; no library method writes to `Console`.
- Request/response models such as `ChatTurn`, `CodexChatResponse`, `CodexCredentials`, `AuthStatus`, and `DeviceCodeLoginInfo`.

The request path mirrors the referenced Zoo Code provider:

- `POST https://chatgpt.com/backend-api/codex/responses` by default
- OAuth Bearer authentication rather than a normal OpenAI API key
- Codex-style headers: `originator`, `session_id`, `User-Agent`, and optional `ChatGPT-Account-Id`
- Responses API-style streaming payloads and SSE parsing for events such as `response.output_text.delta`

> Treat `auth.json` like a password. It contains access and refresh tokens. Do not commit it, paste it into tickets, or share it in chat.

## Install or reference

Install the .NET 10 SDK before building, packing, or referencing the project.

Reference the project directly while developing:

```bash
dotnet add reference ../CodexDotNet/CodexDotNet.csproj
```

Pack it for NuGet distribution:

```bash
dotnet pack CodexDotNet.csproj -c Release
```

The project includes NuGet metadata and packs this `README.md` into the package.

## Basic usage

```csharp
using CodexDotNet;

CodexOptions options = CodexOptions.FromEnvironment();

using HttpClient httpClient = new()
{
    Timeout = Timeout.InfiniteTimeSpan
};

ICodexAuthManager authManager = new CodexAuthManager(httpClient, options);
ICodexClient codex = new CodexClient(httpClient, options, authManager);

IReadOnlyList<ChatTurn> history =
[
    new("user", "Write a C# function that parses an int safely.")
];

CodexChatResponse response = await codex.ChatAsync(history);
Console.WriteLine(response.Text);
```

Use `StreamChatAsync` when the host wants incremental output:

```csharp
await foreach (string fragment in codex.StreamChatAsync(history, cancellationToken))
{
    await writer.WriteAsync(fragment);
}
```

## Auth usage

Most applications can use the default Codex CLI-compatible auth cache at `~/.codex/auth.json`.

```csharp
AuthStatus status = await authManager.GetStatusAsync(cancellationToken);

if (!status.IsAuthenticated)
{
    CodexAuthCallbacks callbacks = new()
    {
        OnBrowserLoginUrl = url => logger.LogInformation("Open {Url}", url),
        OnNotification = message => logger.LogInformation("{Message}", message.Message)
    };

    await authManager.LoginWithBrowserAsync(callbacks, cancellationToken);
}
```

For remote or headless environments, use device-code login if the backend enables it:

```csharp
CodexAuthCallbacks callbacks = new()
{
    OnDeviceCode = info => logger.LogInformation(
        "Open {Url} and enter {Code}. Expires at {ExpiresAt}.",
        info.VerificationUrl,
        info.UserCode,
        info.ExpiresAt)
};

await authManager.LoginWithDeviceCodeAsync(callbacks, cancellationToken);
```

## Microsoft Agent Framework provider usage

The package references `Microsoft.Agents.AI` and exposes `CodexAgent`, an `AIAgent` custom agent provider that uses `CodexClient` for transport and keeps local session history in the Agent Framework session state.

```csharp
using CodexDotNet;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

ServiceCollection services = new();
services.AddCodexAgent(
    CodexOptions.FromEnvironment(),
    new CodexAgentOptions
    {
        Id = "codex-dotnet",
        Name = "Codex",
        Description = "Codex custom agent for coding assistance."
    });

await using ServiceProvider provider = services.BuildServiceProvider();
AIAgent agent = provider.GetRequiredService<AIAgent>();

AgentSession session = await agent.CreateSessionAsync(cancellationToken);
AgentResponse response = await agent.RunAsync(
    "Write a C# function that parses an int safely.",
    session,
    cancellationToken: cancellationToken);

Console.WriteLine(response.Text);
```

`CodexAgentFrameworkChatClient` remains available as an `IChatClient` adapter for hosts that still want to compose their own `ChatClientAgent`. New Microsoft Agent Framework integrations should prefer resolving `CodexAgent` or `AIAgent` from DI via `AddCodexAgent`.

The chat request path refreshes automatically before expiry and retries once after a `401`. You can also call:

```csharp
await authManager.ForceRefreshAsync(cancellationToken);
await authManager.LogoutAsync(revoke: true, callbacks, cancellationToken);
```

## Configuration

`CodexOptions.FromEnvironment()` reads these optional environment variables:

```bash
export CODEX_MODEL="gpt-5.5"
export CODEX_BASE_URL="https://chatgpt.com/backend-api/codex"
export CODEX_ORIGINATOR="dotnet-library-client"
export CODEX_AUTH_ISSUER="https://auth.openai.com"
export CODEX_HOME="$HOME/.codex"
export CODEX_LOGIN_PORT="1455"
export CHATGPT_ACCOUNT_ID="your-chatgpt-account-id-if-needed"
export CODEX_REASONING_EFFORT="medium"
export CODEX_SYSTEM_PROMPT="You are Codex, a helpful coding assistant."
```

Set `CODEX_REASONING_EFFORT=none` to omit the `reasoning` object entirely.

### Environment token override

You can bypass local login/cache and provide a token directly:

```bash
export CODEX_ACCESS_TOKEN="your-oauth-bearer-token"
```

When `CODEX_ACCESS_TOKEN` is present, the library uses it directly and does not refresh it. Use this only for trusted local sessions or short-lived automation.

## Migration from console usage

The project is now a class library. `Program.cs` is excluded from compilation and the reusable API lives in public types.

Old console flow:

```text
dotnet run --project CodexDotNet.csproj
/login
You> write code
```

New library flow:

1. Reference or install the package.
2. Create `CodexOptions`, `HttpClient`, `CodexAuthManager`, and `CodexClient` in your host application.
3. Surface login URLs, device codes, and warnings through `CodexAuthCallbacks` instead of expecting console output.
4. Call `ChatAsync` for complete text or `StreamChatAsync` for incremental fragments.
5. Store conversation state in your application as `List<ChatTurn>`.

## Auth implementation notes

### Browser login

The browser flow uses OAuth authorization code + PKCE:

1. Generate `code_verifier`, `code_challenge`, and `state`.
2. Start a local callback server at `http://localhost:1455/auth/callback` or `1457`.
3. Open `https://auth.openai.com/oauth/authorize` with the Codex client id and scopes.
4. Exchange the callback `code` at `https://auth.openai.com/oauth/token`.
5. Save `id_token`, `access_token`, `refresh_token`, and `last_refresh` to `auth.json`.

### Device-code login

The device-code flow uses:

- `POST https://auth.openai.com/api/accounts/deviceauth/usercode`
- User opens `https://auth.openai.com/codex/device`
- Poll `POST https://auth.openai.com/api/accounts/deviceauth/token`
- Exchange the returned authorization code at `/oauth/token`

If the server returns that device-code auth is unavailable, use browser login instead.

### Refresh

Cached ChatGPT tokens are refreshed through `POST https://auth.openai.com/oauth/token` with:

```json
{
  "client_id": "app_EMoamEEZ73f0CkXaXp7hrann",
  "grant_type": "refresh_token",
  "refresh_token": "..."
}
```

## Troubleshooting

- `Not logged in`: call `LoginWithBrowserAsync`, call `LoginWithDeviceCodeAsync`, or set `CODEX_ACCESS_TOKEN`.
- Browser login hangs: check that localhost callback ports `1455` or `1457` are reachable. In SSH sessions, either forward the port or use device-code login.
- Device-code login unavailable: enable device-code login in ChatGPT/Codex settings if available, or use browser login.
- `401`: token expired or was revoked. The client retries once after refresh; if it still fails, log in again.
- `403`: the account may not have access to the requested Codex model or may need `CHATGPT_ACCOUNT_ID`.
- `400`: try `CODEX_REASONING_EFFORT=none` or a different `CODEX_MODEL`.
- Streaming connects but returns no output: the backend may be sending a new event shape. Update `CodexClient.ProcessJsonEvent` to parse it.
