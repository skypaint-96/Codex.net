using System.Text;
using CodexConsoleChat;

CodexOptions options = CodexOptions.FromEnvironment();

using HttpClient httpClient = new()
{
    Timeout = Timeout.InfiniteTimeSpan
};

CodexAuthManager authManager = new(httpClient, options);
CodexClient codex = new(httpClient, options, authManager);
List<ChatTurn> history = [];

Console.WriteLine("Codex Console Chat");
Console.WriteLine($"Model: {options.Model}");
Console.WriteLine($"Endpoint: {options.BaseUrl.TrimEnd('/')}/responses");
Console.WriteLine($"Auth cache: {authManager.AuthFilePath}");
Console.WriteLine("Commands: /login, /login-device, /status, /refresh, /logout, /clear, /help, /exit");
Console.WriteLine();

await PrintAuthStatusAsync(authManager);
Console.WriteLine();

while (true)
{
    Console.Write("You> ");
    string? userText = Console.ReadLine();

    if (userText is null)
    {
        break;
    }

    userText = userText.Trim();
    if (userText.Length == 0)
    {
        continue;
    }

    if (userText.StartsWith('/'))
    {
        bool handled = await HandleCommandAsync(userText, authManager, history);
        if (handled)
        {
            Console.WriteLine();
            continue;
        }
    }

    history.Add(new ChatTurn("user", userText));

    using CancellationTokenSource requestCts = new();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        requestCts.Cancel();
    };

    Console.CancelKeyPress += cancelHandler;

    StringBuilder assistantText = new();
    Console.Write("Codex> ");

    try
    {
        await foreach (string fragment in codex.StreamChatAsync(history, requestCts.Token))
        {
            Console.Write(fragment);
            assistantText.Append(fragment);
        }

        Console.WriteLine();

        if (assistantText.Length == 0)
        {
            Console.WriteLine("[No text returned by Codex.]");
        }
        else
        {
            history.Add(new ChatTurn("assistant", assistantText.ToString()));
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine();
        Console.WriteLine("[Cancelled]");
        RemoveLastUserTurn(history);
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.Error.WriteLine($"Error: {ex.Message}");
        RemoveLastUserTurn(history);
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }

    Console.WriteLine();
}

return 0;

static async Task<bool> HandleCommandAsync(
    string commandText,
    CodexAuthManager authManager,
    List<ChatTurn> history)
{
    string command = commandText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0]
        .ToLowerInvariant();

    switch (command)
    {
        case "/exit":
        case "/quit":
            Environment.Exit(0);
            return true;

        case "/help":
            PrintHelp();
            return true;

        case "/clear":
            history.Clear();
            Console.WriteLine("Conversation cleared.");
            return true;

        case "/login":
            await RunAuthActionAsync("Browser login", async ct =>
            {
                CodexCredentials credentials = await authManager.LoginWithBrowserAsync(CreateConsoleAuthCallbacks(), ct);
                Console.WriteLine($"Signed in. Token source: {credentials.Source}");
            });
            return true;

        case "/login-device":
            await RunAuthActionAsync("Device-code login", async ct =>
            {
                CodexCredentials credentials = await authManager.LoginWithDeviceCodeAsync(CreateConsoleAuthCallbacks(), ct);
                Console.WriteLine($"Signed in. Token source: {credentials.Source}");
            });
            return true;

        case "/status":
            await PrintAuthStatusAsync(authManager);
            return true;

        case "/refresh":
            await RunAuthActionAsync("Token refresh", async ct =>
            {
                CodexCredentials credentials = await authManager.ForceRefreshAsync(ct);
                Console.WriteLine($"Refreshed. Token source: {credentials.Source}");
            });
            return true;

        case "/logout":
            await RunAuthActionAsync("Logout", async ct =>
            {
                await authManager.LogoutAsync(revoke: true, CreateConsoleAuthCallbacks(), ct);
                history.Clear();
                Console.WriteLine("Logged out and cleared local conversation history.");
            });
            return true;

        case "/auth-file":
            Console.WriteLine(authManager.AuthFilePath);
            return true;

        default:
            Console.WriteLine($"Unknown command: {command}. Type /help for commands.");
            return true;
    }
}

static async Task RunAuthActionAsync(
    string label,
    Func<CancellationToken, Task> action)
{
    using CancellationTokenSource cts = new();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cts.Cancel();
    };

    Console.CancelKeyPress += cancelHandler;
    try
    {
        await action(cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"{label} cancelled.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{label} failed: {ex.Message}");
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task PrintAuthStatusAsync(CodexAuthManager authManager)
{
    AuthStatus status = await authManager.GetStatusAsync();
    Console.WriteLine(status.IsAuthenticated ? "Auth: signed in" : "Auth: not signed in");
    Console.WriteLine($"Source: {status.Source}");

    if (!string.IsNullOrWhiteSpace(status.Email))
    {
        Console.WriteLine($"Email: {status.Email}");
    }

    if (!string.IsNullOrWhiteSpace(status.AccountId))
    {
        Console.WriteLine($"Account/workspace: {status.AccountId}");
    }

    if (!string.IsNullOrWhiteSpace(status.PlanType))
    {
        Console.WriteLine($"Plan: {status.PlanType}");
    }

    if (status.ExpiresAt.HasValue)
    {
        Console.WriteLine($"Access token expires: {status.ExpiresAt:O}");
    }

    if (!string.IsNullOrWhiteSpace(status.Message))
    {
        Console.WriteLine(status.Message);
    }

    if (!status.IsAuthenticated)
    {
        Console.WriteLine("Run /login for browser login, /login-device for device-code login, or set CODEX_ACCESS_TOKEN.");
    }
}

static void PrintHelp()
{
    Console.WriteLine("Commands:");
    Console.WriteLine("  /login         Sign in with ChatGPT in your browser and cache tokens locally.");
    Console.WriteLine("  /login-device  Sign in with a device code for remote/headless terminals.");
    Console.WriteLine("  /status        Show the current auth source without printing secrets.");
    Console.WriteLine("  /refresh       Force-refresh the cached ChatGPT access token.");
    Console.WriteLine("  /logout        Best-effort revoke cached tokens and delete auth.json.");
    Console.WriteLine("  /auth-file     Print the auth cache path.");
    Console.WriteLine("  /clear         Clear in-memory conversation history.");
    Console.WriteLine("  /exit          Quit.");
}

static CodexAuthCallbacks CreateConsoleAuthCallbacks()
{
    return new CodexAuthCallbacks
    {
        OnNotification = notification =>
        {
            TextWriter writer = notification.Level == CodexAuthNotificationLevel.Warning
                || notification.Level == CodexAuthNotificationLevel.Error
                    ? Console.Error
                    : Console.Out;
            writer.WriteLine(notification.Message);
        },
        OnBrowserLoginUrl = url =>
        {
            Console.WriteLine("If it did not open, paste this URL into your browser:");
            Console.WriteLine(url);
            Console.WriteLine();
        },
        OnDeviceCode = info =>
        {
            Console.WriteLine("Follow these steps to sign in with ChatGPT using a device code:");
            Console.WriteLine($"1. Open: {info.VerificationUrl}");
            Console.WriteLine($"2. Enter code: {info.UserCode}");
            Console.WriteLine($"The code expires at {info.ExpiresAt:O}. Never share this code with anyone.");
            Console.WriteLine();
        }
    };
}

static void RemoveLastUserTurn(List<ChatTurn> history)
{
    if (history.Count > 0 && history[^1].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
    {
        history.RemoveAt(history.Count - 1);
    }
}
