using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.AiAgent.Contracts;
using Xunit;

namespace TaskForge.AiAgent.Tests;

public sealed class PolishDraftContractTests
{
    [Fact]
    public void ClaimedJob_ReadsScenarioAndPolishRequestPayload()
    {
        var runId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var courseId = Guid.NewGuid();
        var beforeId = Guid.NewGuid();
        var json = $$"""
        {
          "id": "{{runId}}",
          "type": "polish_assignment_draft",
          "jobType": "polish_assignment_draft",
          "payload": {
            "conversationId": "{{conversationId}}",
            "courseId": "{{courseId}}",
            "rawText": "polish selected task",
            "request": {
              "action": "polish_assignment_draft",
              "taskIndex": 3,
              "beforeAssignmentId": "{{beforeId}}",
              "selectedTask": { "title": "Old", "description": "Needs polish" }
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var job = ClaimedAgentJob.FromJobElement(doc.RootElement);

        Assert.Equal(runId, job.RunId);
        Assert.Equal("polish_assignment_draft", job.JobType);
        Assert.Equal(conversationId, job.ConversationId);
        Assert.Equal(courseId, job.CourseId);
        Assert.Equal(3, job.Payload.GetProperty("request").GetIntOrNull("taskIndex"));
        Assert.Equal(beforeId, job.Payload.GetProperty("request").GetGuidOrNull("beforeAssignmentId"));
    }

    [Fact]
    public void DraftArtifactPreservesPolishPlacementFields()
    {
        var courseId = Guid.NewGuid();
        var beforeId = Guid.NewGuid();
        var afterId = Guid.NewGuid();
        var draft = new DraftSpec
        {
            AssignmentType = "code-test",
            Title = "Polished",
            Description = "Condition",
            Language = "cpp",
            ReferenceSolution = "int main(){return 0;}",
            CourseId = courseId,
            BeforeAssignmentId = beforeId,
            AfterAssignmentId = afterId,
            SourceTaskIndex = 7,
            PublicTests = [new TestCaseSpec { Input = "", ExpectedOutput = "", IsHidden = false }],
            Extra = new JsonObject { ["selectedTask"] = new JsonObject { ["title"] = "Original" } }
        };

        var data = draft.ToArtifactData();

        Assert.Equal(courseId.ToString(), data["courseId"]?.ToString());
        Assert.Equal(beforeId.ToString(), data["beforeAssignmentId"]?.ToString());
        Assert.Equal(afterId.ToString(), data["afterAssignmentId"]?.ToString());
        Assert.Equal("7", data["sourceTaskIndex"]?.ToString());
        var extra = Assert.IsType<JsonObject>(data["extra"]);
        Assert.NotNull(extra["selectedTask"]);
    }
}
