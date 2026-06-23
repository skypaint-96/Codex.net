# CodexConsoleChat

A basic .NET 8 console chatbot that connects to the Codex backend and includes a small login/token-management component.

The request path mirrors the referenced Zoo Code provider:

- `POST https://chatgpt.com/backend-api/codex/responses` by default
- OAuth Bearer authentication rather than a normal OpenAI API key
- Codex-style headers: `originator`, `session_id`, `User-Agent`, and optional `ChatGPT-Account-Id`
- Responses API-style streaming payloads and SSE parsing for events such as `response.output_text.delta`

## What changed in this version

The app no longer requires `CODEX_ACCESS_TOKEN` on every run. It can now sign in and manage the bearer token itself:

- `/login` starts a local OAuth PKCE browser login on `localhost:1455`, with fallback to `1457`.
- `/login-device` uses the Codex device-code flow for remote/headless terminals when the server enables it.
- `/status` shows the current auth source without printing tokens.
- `/refresh` force-refreshes the cached ChatGPT access token.
- `/logout` best-effort revokes cached OAuth tokens and deletes the local cache.
- The chat request path refreshes automatically before expiry and retries once after a `401`.

The login cache is written to `~/.codex/auth.json` by default so it follows the same default file location as Codex CLI file-based auth. You can change that with `CODEX_HOME`.

> Treat `auth.json` like a password. It contains access and refresh tokens. Do not commit it, paste it into tickets, or share it in chat.

## Requirements

- .NET 8 SDK or newer
- A ChatGPT account/workspace with Codex access
- Browser login requires a local browser that can reach `http://localhost:1455/auth/callback` or `http://localhost:1457/auth/callback`
- Device-code login must be enabled for your account/workspace

## Run

```bash
dotnet run --project CodexConsoleChat.csproj
```

On first run, use one of these inside the app:

```text
/login
/login-device
```

Then chat normally:

```text
You> write a C# function that parses an int safely
Codex> ...
```

## Commands

```text
/login         Sign in with ChatGPT in your browser and cache tokens locally.
/login-device  Sign in with a device code for remote/headless terminals.
/status        Show the current auth source without printing secrets.
/refresh       Force-refresh the cached ChatGPT access token.
/logout        Best-effort revoke cached tokens and delete auth.json.
/auth-file     Print the auth cache path.
/clear         Clear in-memory conversation history.
/exit          Quit.
```

Press Ctrl+C while Codex is streaming to cancel the current answer.

## Configuration

Most users can run without environment variables after `/login`.

Optional:

```bash
export CODEX_MODEL="gpt-5.3-codex"
export CODEX_BASE_URL="https://chatgpt.com/backend-api/codex"
export CODEX_ORIGINATOR="dotnet-console-chat"
export CODEX_AUTH_ISSUER="https://auth.openai.com"
export CODEX_HOME="$HOME/.codex"
export CODEX_LOGIN_PORT="1455"
export CHATGPT_ACCOUNT_ID="your-chatgpt-account-id-if-needed"
export CODEX_REASONING_EFFORT="medium"
export CODEX_SYSTEM_PROMPT="You are Codex, a helpful coding assistant."
```

PowerShell equivalent:

```powershell
$env:CODEX_MODEL = "gpt-5.3-codex"
$env:CODEX_HOME = "$HOME\.codex"
dotnet run --project CodexConsoleChat.csproj
```

Set `CODEX_REASONING_EFFORT=none` to omit the `reasoning` object entirely.

### Environment token override

You can still bypass local login/cache and provide a token directly:

```bash
export CODEX_ACCESS_TOKEN="your-oauth-bearer-token"
```

When `CODEX_ACCESS_TOKEN` is present, the app uses it directly and does not refresh it. Use this only for trusted local sessions or short-lived automation.

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

If the server returns that device-code auth is unavailable, use `/login` instead.

### Refresh

Cached ChatGPT tokens are refreshed through `POST https://auth.openai.com/oauth/token` with:

```json
{
  "client_id": "app_EMoamEEZ73f0CkXaXp7hrann",
  "grant_type": "refresh_token",
  "refresh_token": "..."
}
```

## Project files

```text
CodexConsoleChat.csproj
Program.cs
CodexOptions.cs
CodexAuthManager.cs
CodexCredentials.cs
AuthStatus.cs
CodexClient.cs
ChatTurn.cs
.env.example
.gitignore
```

## Troubleshooting

- `Not logged in`: run `/login` or `/login-device`, or set `CODEX_ACCESS_TOKEN`.
- Browser login hangs: check that localhost callback ports `1455` or `1457` are reachable. In SSH sessions, either forward the port or use `/login-device`.
- Device-code login unavailable: enable device-code login in ChatGPT/Codex settings if available, or use `/login`.
- `401`: token expired or was revoked. The app retries once after refresh; if it still fails, run `/login` again.
- `403`: the account may not have access to the requested Codex model or may need `CHATGPT_ACCOUNT_ID`.
- `400`: try `CODEX_REASONING_EFFORT=none` or a different `CODEX_MODEL`.
- Streaming connects but prints no output: the backend may be sending a new event shape. Update `CodexClient.ProcessJsonEvent` to parse it.
