using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CodexDotNet;

public sealed class CodexAgent : AIAgent
{
    private const string HistoryStateKey = "codex.history";

    private readonly ICodexClient _codexClient;
    private readonly CodexOptions _codexOptions;
    private readonly CodexAgentOptions _agentOptions;
    private readonly AIAgentMetadata _metadata;

    public CodexAgent(ICodexClient codexClient, CodexOptions codexOptions, CodexAgentOptions agentOptions)
    {
        _codexClient = codexClient ?? throw new ArgumentNullException(nameof(codexClient));
        _codexOptions = codexOptions ?? throw new ArgumentNullException(nameof(codexOptions));
        _agentOptions = agentOptions ?? throw new ArgumentNullException(nameof(agentOptions));
        _metadata = new AIAgentMetadata("ChatGPT Codex");
        InitializeIdentity();
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(AIAgentMetadata))
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

        return base.GetService(serviceType, serviceKey);
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<AgentSession>(new CodexAgentSession());
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession? session,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        JsonElement serialized = session.StateBag.Serialize();
        return ValueTask.FromResult(serialized);
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement state,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken = default)
    {
        AgentSessionStateBag stateBag = AgentSessionStateBag.Deserialize(state);
        return ValueTask.FromResult<AgentSession>(new CodexAgentSession(stateBag));
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(session);

        List<ChatTurn> requestTurns = BuildRequestTurns(messages, session);
        CodexChatResponse codexResponse = await _codexClient.ChatAsync(requestTurns, cancellationToken);
        ChatMessage assistantMessage = new(ChatRole.Assistant, codexResponse.Text);
        AgentResponse response = new(assistantMessage)
        {
            AgentId = Id,
            CreatedAt = DateTimeOffset.UtcNow,
            FinishReason = ChatFinishReason.Stop,
            RawRepresentation = codexResponse,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["provider"] = "ChatGPT Codex",
                ["model"] = _agentOptions.DefaultModel ?? _codexOptions.Model
            }
        };

        AppendToHistory(session, messages, assistantMessage);
        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(session);

        List<ChatMessage> requestMessages = messages.ToList();
        List<ChatTurn> requestTurns = BuildRequestTurns(requestMessages, session);
        StringBuilder assistantText = new();
        string responseId = Guid.NewGuid().ToString("N");

        await foreach (string fragment in _codexClient.StreamChatAsync(requestTurns, cancellationToken))
        {
            assistantText.Append(fragment);
            yield return new AgentResponseUpdate(ChatRole.Assistant, fragment)
            {
                AgentId = Id,
                ResponseId = responseId,
                CreatedAt = DateTimeOffset.UtcNow,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["provider"] = "ChatGPT Codex",
                    ["model"] = _agentOptions.DefaultModel ?? _codexOptions.Model
                }
            };
        }

        ChatMessage assistantMessage = new(ChatRole.Assistant, assistantText.ToString());
        AppendToHistory(session, requestMessages, assistantMessage);
    }

    private List<ChatTurn> BuildRequestTurns(IEnumerable<ChatMessage> messages, AgentSession session)
    {
        List<ChatTurn> turns = [];
        string instructions = _agentOptions.GetInstructions(_codexOptions);
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            turns.Add(new ChatTurn("system", instructions));
        }

        if (_agentOptions.EnableLocalHistory)
        {
            turns.AddRange(GetHistory(session));
        }

        foreach (ChatMessage message in messages)
        {
            ChatTurn? turn = CodexAgentMessageMapper.ToChatTurn(message);
            if (turn is not null)
            {
                turns.Add(turn);
            }
        }

        return turns;
    }

    private static List<ChatTurn> GetHistory(AgentSession session)
    {
        return session.StateBag.TryGetValue(HistoryStateKey, out List<ChatTurn>? history, null) && history is not null
            ? history
            : [];
    }

    private void AppendToHistory(AgentSession session, IEnumerable<ChatMessage> requestMessages, ChatMessage assistantMessage)
    {
        if (!_agentOptions.EnableLocalHistory)
        {
            return;
        }

        List<ChatTurn> history = GetHistory(session);
        foreach (ChatMessage message in requestMessages)
        {
            ChatTurn? turn = CodexAgentMessageMapper.ToChatTurn(message);
            if (turn is not null)
            {
                history.Add(turn);
            }
        }

        ChatTurn? assistantTurn = CodexAgentMessageMapper.ToChatTurn(assistantMessage);
        if (assistantTurn is not null)
        {
            history.Add(assistantTurn);
        }

        session.StateBag.SetValue(HistoryStateKey, history, null);
    }

    private void InitializeIdentity()
    {
        SetInheritedField("<Id>k__BackingField", _agentOptions.Id);
        SetInheritedField("<Name>k__BackingField", _agentOptions.Name);
        SetInheritedField("<Description>k__BackingField", _agentOptions.Description);
    }

    private void SetInheritedField(string fieldName, string value)
    {
        typeof(AIAgent).GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(this, value);
    }

    private sealed class CodexAgentSession : AgentSession
    {
        public CodexAgentSession()
        {
        }

        public CodexAgentSession(AgentSessionStateBag stateBag)
            : base(stateBag)
        {
        }
    }
}
