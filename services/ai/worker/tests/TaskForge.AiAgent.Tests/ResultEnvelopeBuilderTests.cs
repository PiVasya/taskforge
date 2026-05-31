using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Runtime;
using Xunit;

namespace TaskForge.AiAgent.Tests;

public sealed class ResultEnvelopeBuilderTests
{
    [Fact]
    public void RawAgentJsonIsParsedIntoEnvelope()
    {
        var payload = JsonDocument.Parse("{}").RootElement.Clone();
        var job = new ClaimedAgentJob(Guid.NewGuid(), Guid.NewGuid(), "assistant_chat_turn", payload, "hello", null, null);
        var envelope = ResultEnvelopeBuilder.FromRawAgentText("{\"assistantMessage\":\"ok\",\"artifacts\":[]}", job, "test");
        Assert.Equal("ok", envelope.AssistantMessage);
        Assert.Equal("test", envelope.ScenarioId);
    }
}
