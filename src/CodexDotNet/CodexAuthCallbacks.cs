namespace CodexDotNet;

public sealed class CodexAuthCallbacks
{
    public Action<CodexAuthNotification>? OnNotification { get; init; }

    public Action<string>? OnBrowserLoginUrl { get; init; }

    public Action<DeviceCodeLoginInfo>? OnDeviceCode { get; init; }
}
