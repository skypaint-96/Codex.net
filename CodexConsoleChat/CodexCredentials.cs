namespace CodexConsoleChat;

public sealed record CodexCredentials(
    string AccessToken,
    string? AccountId,
    bool IsEnvironmentOverride,
    string Source);
