namespace CodexDotNet;

public interface ICodexAuthManager
{
    string AuthFilePath { get; }

    Task<bool> HasUsableCredentialsAsync(CancellationToken cancellationToken = default);

    Task<CodexCredentials> GetAccessTokenAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task<CodexCredentials> ForceRefreshAsync(CancellationToken cancellationToken = default);

    Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<CodexCredentials> LoginWithBrowserAsync(
        CodexAuthCallbacks? callbacks = null,
        CancellationToken cancellationToken = default);

    Task<CodexCredentials> LoginWithDeviceCodeAsync(
        CodexAuthCallbacks? callbacks = null,
        CancellationToken cancellationToken = default);

    Task LogoutAsync(
        bool revoke = true,
        CodexAuthCallbacks? callbacks = null,
        CancellationToken cancellationToken = default);
}
