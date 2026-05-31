using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Options;

namespace TaskForge.AiAgent.Llm;

/// <summary>
/// Small direct OpenAI-compatible text client for workflow-internal LLM calls.
///
/// These stages do not need tools or agent sessions: they already receive the
/// full explicit prompt. Calling the chat-completions HTTP endpoint directly
/// avoids Microsoft.Agents.AI / provider-adapter history conversion failures
/// such as OpenAI.ChangeTrackingList.get_Item / ChatCompletion.get_Role().
/// </summary>
public sealed class DirectLlmTextClient
{
    private readonly HttpClient _http;
    private readonly TaskForgeAgentOptions _options;
    private readonly ILogger<DirectLlmTextClient> _logger;

    public DirectLlmTextClient(HttpClient http, IOptions<TaskForgeAgentOptions> options, ILogger<DirectLlmTextClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("TaskForgeAgent:ApiKey is empty. Put OpenRouter/OpenAI key in configuration or env var.");

        var endpoint = BuildEndpoint();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = prompt
                }
            },
            ["temperature"] = 0.2
        };

        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var preview = responseText.Length <= 2000 ? responseText : responseText[..2000] + "...";
            _logger.LogWarning("Direct LLM request failed with {StatusCode}: {Preview}", (int)response.StatusCode, preview);
            throw new InvalidOperationException($"LLM HTTP {(int)response.StatusCode}: {preview}");
        }

        var content = ExtractContent(responseText);
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("LLM response did not contain choices[0].message.content.");

        return content;
    }

    private Uri BuildEndpoint()
    {
        var baseUrl = (_options.OpenAiCompatibleBaseUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("TaskForgeAgent:OpenAiCompatibleBaseUrl is empty.");

        baseUrl = baseUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return new Uri(baseUrl);

        return new Uri(baseUrl + "/chat/completions");
    }

    private static string ExtractContent(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = new List<string>();
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var content))
                {
                    var value = ReadContent(content);
                    if (!string.IsNullOrEmpty(value)) parts.Add(value);
                }
            }
            else if (choice.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                var value = text.GetString();
                if (!string.IsNullOrEmpty(value)) parts.Add(value);
            }
        }

        return string.Join("\n", parts.Where(x => !string.IsNullOrEmpty(x))).Trim();
    }

    private static string ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        // Some OpenAI-compatible providers may return multimodal content parts.
        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    parts.Add(item.GetString() ?? string.Empty);
                    continue;
                }

                if (item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        parts.Add(text.GetString() ?? string.Empty);
                    else if (item.TryGetProperty("content", out var nested) && nested.ValueKind == JsonValueKind.String)
                        parts.Add(nested.GetString() ?? string.Empty);
                }
            }

            return string.Join("", parts);
        }

        return string.Empty;
    }
}
