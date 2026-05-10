using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using TaskForge.AiAgent.Options;

namespace TaskForge.AiAgent.Llm;

public sealed class OpenAiCompatibleChatClientFactory : ITaskForgeChatClientFactory
{
    private readonly TaskForgeAgentOptions _options;
    private readonly ILogger<OpenAiCompatibleChatClientFactory> _logger;

    public OpenAiCompatibleChatClientFactory(IOptions<TaskForgeAgentOptions> options, ILogger<OpenAiCompatibleChatClientFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IChatClient CreateChatClient()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("TaskForgeAgent:ApiKey is empty. Put OpenRouter/OpenAI key in configuration or env var.");

        var endpoint = new Uri(_options.OpenAiCompatibleBaseUrl.TrimEnd('/') + "/");
        _logger.LogInformation("Creating IChatClient provider={Provider} model={Model} endpoint={Endpoint}", _options.Provider, _options.Model, endpoint);

        var client = new OpenAIClient(new ApiKeyCredential(_options.ApiKey), new OpenAIClientOptions
        {
            Endpoint = endpoint
        });

        return client.GetChatClient(_options.Model).AsIChatClient();
    }
}
