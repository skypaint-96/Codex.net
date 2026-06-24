namespace CodexConsoleChat;

public sealed record AuthStatus(
    bool IsAuthenticated,
    string Source,
    string? Email,
    string? AccountId,
    string? PlanType,
    DateTimeOffset? ExpiresAt,
    string AuthFilePath,
    bool IsEnvironmentOverride,
    string? Message);
