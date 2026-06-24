namespace CodexDotNet;

public sealed class CodexAgentOptions
{
    public string Id { get; init; } = "codex-dotnet";
    public string Name { get; init; } = "Codex";
    public string Description { get; init; } = "A Microsoft Agent Framework custom agent backed by ChatGPT Codex.";
    public string? Instructions { get; init; }
    public string? DefaultModel { get; init; }
    public bool EnableLocalHistory { get; init; } = true;

    public string GetInstructions(CodexOptions codexOptions)
    {
        ArgumentNullException.ThrowIfNull(codexOptions);
        return string.IsNullOrWhiteSpace(Instructions)
            ? codexOptions.SystemPrompt
            : Instructions;
    }
}
