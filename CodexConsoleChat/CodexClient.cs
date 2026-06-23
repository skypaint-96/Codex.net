using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexConsoleChat;

public sealed class CodexClient
{
    private sealed class StreamState
    {
        public bool SawText { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly HttpClient _httpClient;
    private readonly CodexOptions _options;
    private readonly CodexAuthManager _authManager;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    public CodexClient(HttpClient httpClient, CodexOptions options, CodexAuthManager authManager)
    {
        _httpClient = httpClient;
        _options = options;
        _authManager = authManager;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<ChatTurn> history,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CodexCredentials credentials = await _authManager.GetAccessTokenAsync(
            forceRefresh: false,
            cancellationToken);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            using HttpRequestMessage request = CreateRequest(history, credentials);

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized
                && attempt == 0
                && !credentials.IsEnvironmentOverride)
            {
                // The backend can reject an access token before the local expiry time.
                // Retry once after forcing a refresh through the saved refresh token.
                _ = await response.Content.ReadAsStringAsync(cancellationToken);
                credentials = await _authManager.GetAccessTokenAsync(
                    forceRefresh: true,
                    cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                string errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Codex request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {errorText}",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            await foreach (string fragment in ReadStreamingTextAsync(response, cancellationToken))
            {
                yield return fragment;
            }

            yield break;
        }
    }

    private static async IAsyncEnumerable<string> ReadStreamingTextAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new(stream, Encoding.UTF8);

        List<string> dataLines = new();
        StreamState state = new();

        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                foreach (string fragment in ProcessSseDataLines(dataLines, state))
                {
                    yield return fragment;
                }

                dataLines.Clear();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line[5..];
                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }

                dataLines.Add(value);
                continue;
            }

            // Some servers/proxies may pass through a bare JSON object per line.
            if (LooksLikeJson(line))
            {
                foreach (string fragment in ProcessJsonEvent(line, state))
                {
                    yield return fragment;
                }
            }
        }

        foreach (string fragment in ProcessSseDataLines(dataLines, state))
        {
            yield return fragment;
        }
    }

    private HttpRequestMessage CreateRequest(IReadOnlyList<ChatTurn> history, CodexCredentials credentials)
    {
        Uri endpoint = new($"{_options.BaseUrl.TrimEnd('/')}/responses");
        HttpRequestMessage request = new(HttpMethod.Post, endpoint);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("originator", _options.Originator);
        request.Headers.TryAddWithoutValidation("session_id", _sessionId);
        request.Headers.TryAddWithoutValidation("User-Agent", BuildUserAgent());

        string? accountId = _options.ChatGptAccountId ?? credentials.AccountId;
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        }

        Dictionary<string, object?> body = new()
        {
            ["model"] = _options.Model,
            ["input"] = FormatConversation(history),
            ["stream"] = true,
            ["store"] = false,
            ["instructions"] = _options.SystemPrompt
        };

        if (_options.HasReasoningEffort())
        {
            body["include"] = new[] { "reasoning.encrypted_content" };
            body["reasoning"] = new
            {
                effort = _options.ReasoningEffort,
                summary = "auto"
            };
        }

        string json = JsonSerializer.Serialize(body, JsonOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static List<object> FormatConversation(IReadOnlyList<ChatTurn> history)
    {
        List<object> input = new(capacity: history.Count);

        foreach (ChatTurn turn in history)
        {
            if (turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            {
                input.Add(new
                {
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = turn.Text
                        }
                    }
                });
            }
            else
            {
                input.Add(new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = turn.Text
                        }
                    }
                });
            }
        }

        return input;
    }

    private static string BuildUserAgent()
    {
        string os = RuntimeInformation.OSDescription.Replace('\n', ' ').Trim();
        string arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        string dotnetVersion = Environment.Version.ToString();
        return $"dotnet-console-chat/1.1 ({os}; {arch}) dotnet/{dotnetVersion}";
    }

    private static IEnumerable<string> ProcessSseDataLines(List<string> dataLines, StreamState state)
    {
        if (dataLines.Count == 0)
        {
            yield break;
        }

        string data = string.Join("\n", dataLines).Trim();
        if (data.Length == 0 || data == "[DONE]")
        {
            yield break;
        }

        foreach (string fragment in ProcessJsonEvent(data, state))
        {
            yield return fragment;
        }
    }

    private static IEnumerable<string> ProcessJsonEvent(string json, StreamState state)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string? errorMessage = TryExtractErrorMessage(root);
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new InvalidOperationException(errorMessage);
        }

        string? type = TryGetStringProperty(root, "type");

        if (type is "response.output_text.delta" or "response.text.delta")
        {
            string? delta = TryGetStringProperty(root, "delta");
            if (!string.IsNullOrEmpty(delta))
            {
                state.SawText = true;
                yield return delta;
            }

            yield break;
        }

        if (type is "response.refusal.delta")
        {
            string? delta = TryGetStringProperty(root, "delta");
            if (!string.IsNullOrEmpty(delta))
            {
                state.SawText = true;
                yield return $"[Refusal] {delta}";
            }

            yield break;
        }

        if (root.TryGetProperty("choices", out JsonElement choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            JsonElement firstChoice = choices[0];
            if (firstChoice.TryGetProperty("delta", out JsonElement delta)
                && TryGetStringProperty(delta, "content") is { Length: > 0 } content)
            {
                state.SawText = true;
                yield return content;
                yield break;
            }
        }

        if (type is "response.completed" or "response.done")
        {
            if (!state.SawText && root.TryGetProperty("response", out JsonElement completedResponse))
            {
                foreach (string text in ExtractOutputText(completedResponse))
                {
                    state.SawText = true;
                    yield return text;
                }
            }

            yield break;
        }

        if (!state.SawText && root.TryGetProperty("response", out JsonElement responseElement))
        {
            foreach (string text in ExtractOutputText(responseElement))
            {
                state.SawText = true;
                yield return text;
            }
        }

        if (!state.SawText)
        {
            foreach (string text in ExtractOutputText(root))
            {
                state.SawText = true;
                yield return text;
            }
        }
    }

    private static IEnumerable<string> ExtractOutputText(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (element.TryGetProperty("output", out JsonElement output)
            && output.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in output.EnumerateArray())
            {
                foreach (string text in ExtractOutputItemText(item))
                {
                    yield return text;
                }
            }
        }
        else
        {
            foreach (string text in ExtractOutputItemText(element))
            {
                yield return text;
            }
        }
    }

    private static IEnumerable<string> ExtractOutputItemText(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (item.TryGetProperty("content", out JsonElement content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement part in content.EnumerateArray())
            {
                string? partType = TryGetStringProperty(part, "type");
                if (partType is "output_text" or "text" or null)
                {
                    string? text = TryGetStringProperty(part, "text")
                        ?? TryGetStringProperty(part, "output_text");

                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return text;
                    }
                }
            }
        }

        string? type = TryGetStringProperty(item, "type");
        if (type is "output_text" or "text" or "message" or null)
        {
            string? text = TryGetStringProperty(item, "text")
                ?? TryGetStringProperty(item, "output_text")
                ?? TryGetStringProperty(item, "content");

            if (!string.IsNullOrEmpty(text))
            {
                yield return text;
            }
        }
    }

    private static string? TryExtractErrorMessage(JsonElement root)
    {
        string? type = TryGetStringProperty(root, "type");
        bool errorType = type is "error" or "response.error" or "response.failed" or "response.incomplete";
        bool hasErrorObject = root.TryGetProperty("error", out JsonElement errorElement);

        if (!errorType && !hasErrorObject)
        {
            return null;
        }

        if (hasErrorObject)
        {
            string? messageFromError = TryGetStringProperty(errorElement, "message")
                ?? TryGetStringProperty(errorElement, "detail")
                ?? TryGetStringProperty(errorElement, "code");

            if (!string.IsNullOrWhiteSpace(messageFromError))
            {
                return $"Codex API error: {messageFromError}";
            }
        }

        string? message = TryGetStringProperty(root, "message")
            ?? TryGetStringProperty(root, "detail")
            ?? TryGetStringProperty(root, "status");

        return string.IsNullOrWhiteSpace(message)
            ? $"Codex API returned an error event: {root}"
            : $"Codex API error: {message}";
    }

    private static string? TryGetStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Object when value.TryGetProperty("value", out JsonElement nested)
                && nested.ValueKind == JsonValueKind.String => nested.GetString(),
            _ => null
        };
    }

    private static bool LooksLikeJson(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }
}
