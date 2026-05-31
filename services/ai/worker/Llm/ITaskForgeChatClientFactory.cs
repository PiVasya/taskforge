using Microsoft.Extensions.AI;

namespace TaskForge.AiAgent.Llm;

public interface ITaskForgeChatClientFactory
{
    IChatClient CreateChatClient();
}
