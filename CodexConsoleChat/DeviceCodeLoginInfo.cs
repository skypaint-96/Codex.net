namespace CodexConsoleChat;

public sealed record DeviceCodeLoginInfo(
    string VerificationUrl,
    string UserCode,
    DateTimeOffset ExpiresAt,
    TimeSpan PollingInterval);
