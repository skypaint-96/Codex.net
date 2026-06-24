using System.Text;
using Microsoft.Extensions.AI;

namespace CodexDotNet;

internal static class CodexAgentMessageMapper
{
    public static ChatTurn? ToChatTurn(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        string text = GetText(message.Contents);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new ChatTurn(ToCodexRole(message.Role), text);
    }

    public static ChatMessage ToChatMessage(ChatTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return new ChatMessage(ToChatRole(turn.Role), turn.Text);
    }

    private static string GetText(IList<AIContent> contents)
    {
        StringBuilder builder = new();

        foreach (AIContent content in contents)
        {
            if (content is TextContent textContent)
            {
                builder.Append(textContent.Text);
            }
        }

        return builder.ToString();
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

    private static ChatRole ToChatRole(string role)
    {
        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
        {
            return ChatRole.Assistant;
        }

        if (role.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            return ChatRole.System;
        }

        return ChatRole.User;
    }
}
