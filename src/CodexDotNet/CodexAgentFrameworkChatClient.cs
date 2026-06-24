using Microsoft.Extensions.AI;

namespace CodexConsoleChat;

/// <summary>
/// Adapts <see cref="ICodexClient" /> to the Microsoft Agent Framework chat provider contract.
/// </summary>
public sealed class CodexAgentFrameworkChatClient : IChatClient
{
    private readonly ICodexClient _codexClient;
    private readonly ChatClientMetadata _metadata;

    public CodexAgentFrameworkChatClient(ICodexClient codexClient, CodexOptions options)
    {
        ArgumentNullException.ThrowIfNull(codexClient);
        ArgumentNullException.ThrowIfNull(options);

        _codexClient = codexClient;
        _metadata = new ChatClientMetadata(
            providerName: "ChatGPT Codex",
            providerUri: new Uri(options.BaseUrl),
            defaultModelId: options.Model);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        IReadOnlyList<ChatTurn> history = ToCodexHistory(messages, options);
        CodexChatResponse response = await _codexClient.ChatAsync(history, cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, response.Text));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        IReadOnlyList<ChatTurn> history = ToCodexHistory(messages, options);
        await foreach (string fragment in _codexClient.StreamChatAsync(history, cancellationToken))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, fragment);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return _metadata;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        if (serviceType.IsInstanceOfType(_codexClient))
        {
            return _codexClient;
        }

        return null;
    }

    public void Dispose()
    {
    }

    private static IReadOnlyList<ChatTurn> ToCodexHistory(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        List<ChatTurn> history = [];

        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            history.Add(new ChatTurn("system", options.Instructions));
        }

        foreach (ChatMessage message in messages)
        {
            string? text = message.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            history.Add(new ChatTurn(ToCodexRole(message.Role), text));
        }

        return history;
    }

    private static string ToCodexRole(ChatRole role)
    {
        if (role == ChatRole.Assistant)
        {
            return "assistant";
        }

        if (role == ChatRole.System)
        {
            return "system";
        }

        return "user";
    }
}
