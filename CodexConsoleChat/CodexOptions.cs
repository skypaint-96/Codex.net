namespace CodexConsoleChat;

public sealed class CodexOptions
{
    public const string DefaultBaseUrl = "https://chatgpt.com/backend-api/codex";
    public const string DefaultAuthIssuer = "https://auth.openai.com";
    public const string DefaultModel = "gpt-5.3-codex";
    public const int DefaultLoginPort = 1455;

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string Model { get; init; } = DefaultModel;
    public string Originator { get; init; } = "dotnet-console-chat";
    public string? ChatGptAccountId { get; init; }
    public string? ReasoningEffort { get; init; } = "medium";
    public string AuthIssuer { get; init; } = DefaultAuthIssuer;
    public string CodexHome { get; init; } = GetDefaultCodexHome();
    public int LoginPort { get; init; } = DefaultLoginPort;
    public string SystemPrompt { get; init; } =
        "You are Codex, a helpful coding assistant running in a .NET console chatbot. " +
        "Answer clearly and ask concise follow-up questions only when required.";

    public static CodexOptions FromEnvironment()
    {
        return new CodexOptions
        {
            BaseUrl = ReadEnv("CODEX_BASE_URL", DefaultBaseUrl).TrimEnd('/'),
            Model = ReadEnv("CODEX_MODEL", DefaultModel),
            Originator = ReadEnv("CODEX_ORIGINATOR", "dotnet-console-chat"),
            ChatGptAccountId = EmptyToNull(Environment.GetEnvironmentVariable("CHATGPT_ACCOUNT_ID")),
            ReasoningEffort = EmptyToNull(Environment.GetEnvironmentVariable("CODEX_REASONING_EFFORT")) ?? "medium",
            AuthIssuer = ReadEnv("CODEX_AUTH_ISSUER", DefaultAuthIssuer).TrimEnd('/'),
            CodexHome = ReadEnv("CODEX_HOME", GetDefaultCodexHome()),
            LoginPort = ReadIntEnv("CODEX_LOGIN_PORT", DefaultLoginPort),
            SystemPrompt = ReadEnv(
                "CODEX_SYSTEM_PROMPT",
                "You are Codex, a helpful coding assistant running in a .NET console chatbot. " +
                "Answer clearly and ask concise follow-up questions only when required.")
        };
    }

    public bool HasReasoningEffort()
    {
        if (string.IsNullOrWhiteSpace(ReasoningEffort))
        {
            return false;
        }

        return !ReasoningEffort.Equals("none", StringComparison.OrdinalIgnoreCase)
            && !ReasoningEffort.Equals("disable", StringComparison.OrdinalIgnoreCase)
            && !ReasoningEffort.Equals("disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadEnv(string name, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int ReadIntEnv(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string GetDefaultCodexHome()
    {
        string? home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.CurrentDirectory;
        }

        return Path.Combine(home, ".codex");
    }
}
