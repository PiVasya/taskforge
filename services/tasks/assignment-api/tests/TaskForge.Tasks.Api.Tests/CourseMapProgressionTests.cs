using System.Text.Json;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Services.Access;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class CourseMapProgressionTests
{
    [Fact]
    public void HiddenMerge_RemainsHiddenUntilEveryIncomingPrerequisiteIsSolved()
    {
        var root = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var target = Guid.NewGuid();
        using var document = JsonDocument.Parse($$$"""
        {
          "nodes": [
            {"id":"root","type":"course","entityId":"{{{root}}}","position":{"x":0,"y":0}},
            {"id":"a","type":"assignment","entityId":"{{{a}}}","position":{"x":100,"y":0}},
            {"id":"b","type":"assignment","entityId":"{{{b}}}","position":{"x":100,"y":100}},
            {"id":"target","type":"assignment","entityId":"{{{target}}}","position":{"x":200,"y":50}}
          ],
          "edges": [
            {"id":"root-a","source":"root","target":"a"},
            {"id":"root-b","source":"root","target":"b"},
            {"id":"a-target","source":"a","target":"target","settings":{"hiddenEffect":"start"}},
            {"id":"b-target","source":"b","target":"target","settings":{"hiddenEffect":"start"}}
          ]
        }
        """);

        var tree = new CourseTreeResponse
        {
            CourseId = root,
            CourseIds = [root],
            Courses = [new CourseTreeCourseDto { Id = root, Title = "Root", IsPublic = true }]
        };
        var rows = new List<CourseMapProgressionService.AssignmentProgressionRow>
        {
            new(a, root, "A", "code-test", "cpp", null, null, true, 0),
            new(b, root, "B", "code-test", "cpp", null, null, true, 1),
            new(target, root, "Target", "code-test", "cpp", null, null, true, 2),
        };
        var accessible = new HashSet<Guid> { root };

        var oneSolved = CourseMapProgressionService.EvaluatePrepared(
            root, root, 1, document.RootElement, tree, accessible, rows, [a], null, null);
        var allSolved = CourseMapProgressionService.EvaluatePrepared(
            root, root, 1, document.RootElement, tree, accessible, rows, [a, b], null, null);

        Assert.DoesNotContain(target, oneSolved.VisibleAssignmentIds);
        Assert.Contains(target, allSolved.VisibleAssignmentIds);
    }
}
