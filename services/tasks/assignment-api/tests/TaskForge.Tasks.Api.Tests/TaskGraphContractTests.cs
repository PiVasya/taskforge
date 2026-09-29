using System.Text.Json;
using TaskForge.Tasks.Api.Services.Serialization;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class TaskGraphContractTests
{
    [Fact]
    public void CanonicalGraphContract_StaysBackwardCompatible()
    {
        Assert.Equal(5, AssignmentTaskGraphJsonService.SchemaVersion);
        Assert.Equal(4, AssignmentTaskGraphJsonService.PreviousSchemaVersion);
        Assert.Equal(3, AssignmentTaskGraphJsonService.LegacySchemaVersion);
        Assert.Equal(5000, AssignmentTaskGraphJsonService.MaxTasks);
    }

    [Fact]
    public void CanonicalGraph_AllowsExplicitTaskDeletionById()
    {
        using var document = JsonDocument.Parse("""
        {
          "schemaVersion": 5,
          "format": "taskforge-task-graph",
          "scopes": ["ids", "connections"],
          "courses": [],
          "tasks": [],
          "deleteTasks": ["11111111-1111-4111-8111-111111111111"],
          "datasets": [],
          "connections": []
        }
        """);

        var parsed = AssignmentTaskGraphJsonService.ParseAndValidate(document.RootElement);

        Assert.Empty(parsed.Issues);
        Assert.NotNull(parsed.Graph);
        Assert.Equal(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Assert.Single(parsed.Graph!.DeleteTaskIds));
    }

    [Fact]
    public void CanonicalGraph_RejectsUpdatingAndDeletingSameTask()
    {
        using var document = JsonDocument.Parse("""
        {
          "schemaVersion": 5,
          "format": "taskforge-task-graph",
          "scopes": ["ids", "content", "connections"],
          "courses": [],
          "tasks": [
            {
              "key": "task-1",
              "id": "11111111-1111-4111-8111-111111111111",
              "course": "$course",
              "type": "test",
              "title": "Задание"
            }
          ],
          "deleteTasks": ["11111111-1111-4111-8111-111111111111"],
          "datasets": [],
          "connections": []
        }
        """);

        var parsed = AssignmentTaskGraphJsonService.ParseAndValidate(document.RootElement);

        Assert.Null(parsed.Graph);
        Assert.Contains(parsed.Issues, issue => issue.Path == "$.deleteTasks[0]");
    }

    [Fact]
    public void CanonicalGraph_RequiresIdsScopeForDeletion()
    {
        using var document = JsonDocument.Parse("""
        {
          "schemaVersion": 5,
          "format": "taskforge-task-graph",
          "scopes": ["connections"],
          "courses": [],
          "tasks": [],
          "deleteTasks": ["11111111-1111-4111-8111-111111111111"],
          "datasets": [],
          "connections": []
        }
        """);

        var parsed = AssignmentTaskGraphJsonService.ParseAndValidate(document.RootElement);

        Assert.Null(parsed.Graph);
        Assert.Contains(parsed.Issues, issue => issue.Path == "$.scopes" && issue.Message.Contains("deleteTasks"));
    }
    [Fact]
    public void CanonicalGraph_RejectsDeleteTasksOnOlderSchemaVersions()
    {
        using var document = JsonDocument.Parse("""
        {
          "schemaVersion": 4,
          "format": "taskforge-task-graph",
          "scopes": ["ids", "connections"],
          "courses": [],
          "tasks": [],
          "deleteTasks": ["11111111-1111-4111-8111-111111111111"],
          "connections": []
        }
        """);

        var parsed = AssignmentTaskGraphJsonService.ParseAndValidate(document.RootElement);

        Assert.Null(parsed.Graph);
        Assert.Contains(parsed.Issues, issue => issue.Path == "$.schemaVersion" && issue.Message.Contains("deleteTasks"));
    }

}
