namespace CodexDotNet;

public sealed record CodexAuthNotification(
    CodexAuthNotificationLevel Level,
    string Message,
    Exception? Exception = null);
