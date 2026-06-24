namespace CodexDotNet;

public interface ICodexClient
{
    IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<ChatTurn> history,
        CancellationToken cancellationToken = default);

    Task<CodexChatResponse> ChatAsync(
        IReadOnlyList<ChatTurn> history,
        CancellationToken cancellationToken = default);
}
