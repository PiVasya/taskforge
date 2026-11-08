using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Serialization;
using TaskForge.Tasks.Api.Services.Sql;
using Xunit;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentTaskGraphJsonService;

namespace TaskForge.Tasks.Api.Tests;

public sealed class TaskGraphImportRegressionTests
{
    private static readonly Guid Root = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Sub = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Task = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Foreign = Guid.Parse("44444444-4444-4444-8444-444444444444");

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DisabledTopology_AllowsMissingConnectionsAndDatasets(int version)
    {
        var root = JsonSerializer.SerializeToElement(new { schemaVersion = version, format = Format,
            scopes = new[] { "ids", "content" }, tasks = Array.Empty<object>() });
        var parsed = ParseAndValidate(root);
        Assert.Empty(parsed.Issues);
        Assert.NotNull(parsed.Graph);
        Assert.False(parsed.Graph!.Scopes.Contains("connections"));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void OlderVersion_AllowsEmptyDatasetsButRejectsRealResources(int version)
    {
        var empty = JsonSerializer.SerializeToElement(new { schemaVersion = version, format = Format,
            scopes = new[] { "content" }, tasks = Array.Empty<object>(), datasets = Array.Empty<object>() });
        Assert.Empty(ParseAndValidate(empty).Issues);
        var withResource = JsonSerializer.SerializeToElement(new { schemaVersion = version, format = Format,
            scopes = new[] { "content" }, tasks = Array.Empty<object>(), datasets = new[] { new { key = "db" } } });
        Assert.Contains(ParseAndValidate(withResource).Issues, x => x.Path == "$.datasets");
    }

    [Fact]
    public void MissingConnections_RequiresArrayOnlyForActiveTopologyOrEffects()
    {
        var input = JsonSerializer.SerializeToElement(new { schemaVersion = 5, format = Format,
            scopes = new[] { "connections", "connectionAccess" }, tasks = Array.Empty<object>() });
        Assert.Contains(ParseAndValidate(input).Issues, x => x.Path == "$.connections");
        Assert.Empty(ParseAndValidate(input, new GraphImportOptions(UpdateConnections: false, UpdateConnectionAccess: false)).Issues);
        Assert.Contains(ParseAndValidate(input, new GraphImportOptions(UpdateConnections: false)).Issues, x => x.Path == "$.connections");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NewSqlTask_InitializesItsSpecRegardlessOfExistingUpdateSwitches(bool content, bool checks)
    {
        var selected = new GraphImportOptions(UpdateContent: content, UpdateChecks: checks);
        var newTask = SqlTaskGraphService.OptionsForTask(true, selected);
        Assert.True(newTask.UpdateContent);
        Assert.True(newTask.UpdateChecks);
        Assert.Equal(selected, SqlTaskGraphService.OptionsForTask(false, selected));
    }

    private static ParsedGraph BindingsGraph(bool includeParent = false)
    {
        var task = new Dictionary<string, object> { ["key"] = "a", ["id"] = Task };
        if (includeParent) task["course"] = "sub";
        var input = JsonSerializer.SerializeToElement(new { schemaVersion = 5, format = Format,
            scopes = new[] { "ids" }, courses = new[] { new { key = "sub", id = Sub, title = "Subcourse" } }, tasks = new[] { task } });
        var parsed = ParseAndValidate(input);
        Assert.Empty(parsed.Issues);
        return Assert.IsType<ParsedGraph>(parsed.Graph);
    }

    [Fact]
    public void GlobalTaskUuidConflict_IsRejectedWithoutReturningForeignMetadata()
    {
        var issues = ValidateBindings(BindingsGraph(), Root, new HashSet<Guid> { Root, Sub },
            new Dictionary<Guid, Guid> { [Task] = Foreign }, new HashSet<Guid>());
        Assert.Equal("$.tasks[0].id", Assert.Single(issues).Path);
        Assert.DoesNotContain(Foreign.ToString(), issues[0].Message);
    }

    [Fact]
    public void GlobalCourseUuidConflict_IsRejectedBeforeEnsureWrites()
    {
        var issues = ValidateBindings(BindingsGraph(), Root, new HashSet<Guid> { Root },
            new Dictionary<Guid, Guid>(), new HashSet<Guid> { Sub });
        Assert.Equal("$.courses[0].id", Assert.Single(issues).Path);
    }

    [Fact]
    public void ExistingTaskWithoutParent_KeepsActualCourse()
    {
        Assert.Empty(ValidateBindings(BindingsGraph(), Root, new HashSet<Guid> { Root, Sub },
            new Dictionary<Guid, Guid> { [Task] = Sub }, new HashSet<Guid> { Sub }));
    }

    [Fact]
    public void ExistingTaskWithExplicitDifferentParent_IsRejected()
    {
        var issues = ValidateBindings(BindingsGraph(includeParent: true), Root, new HashSet<Guid> { Root, Sub },
            new Dictionary<Guid, Guid> { [Task] = Root }, new HashSet<Guid> { Sub });
        Assert.Equal("$.tasks[0].course", Assert.Single(issues).Path);
    }

    [Fact]
    public void FreeIdsRemainValidAndAreNotConsideredDuplicates()
    {
        Assert.Empty(ValidateBindings(BindingsGraph(), Root, new HashSet<Guid> { Root },
            new Dictionary<Guid, Guid>(), new HashSet<Guid>()));
    }

    [Fact]
    public void NoStoredMap_ExportDoesNotInventSequentialConnections()
    {
        var (tree, assignments) = ExportSample();
        var assignmentDtos = new Dictionary<Guid, JsonObject>
        {
            [Task] = new()
            {
                ["id"] = Task.ToString("D"),
                ["type"] = "code-test",
                ["title"] = "Draft",
                ["description"] = string.Empty,
                ["tags"] = string.Empty,
                ["rating"] = 1,
                ["isVisible"] = true,
                ["language"] = "csharp",
                ["allowedLanguages"] = JsonSerializer.SerializeToNode(new[] { "csharp" }),
                ["starterCode"] = string.Empty,
                ["testCases"] = new JsonArray(),
                ["codeForbiddenCalls"] = new JsonArray(),
                ["codeRequiredCalls"] = new JsonArray()
            }
        };

        var exported = BuildExport(Root, assignments, tree, new CourseMapInternalResponse(),
            new GraphExportOptions(), assignmentDtos: assignmentDtos);
        Assert.Empty(exported["connections"]!.AsArray());
        var task = Assert.Single(exported["tasks"]!.AsArray());
        Assert.Equal(Task.ToString("D"), task!["id"]!.GetValue<string>());
        Assert.Equal("Draft", task["title"]!.GetValue<string>());
        Assert.False(exported.ContainsKey("layout"));
        Assert.NotNull(exported["guide"]!["expectedBehavior"]);
    }

    [Fact]
    public void ExportWithoutTypedDto_RejectsIncompleteAssignment()
    {
        var (tree, assignments) = ExportSample();
        var error = Assert.Throws<InvalidOperationException>(() => BuildExport(
            Root, assignments, tree, new CourseMapInternalResponse(), new GraphExportOptions(),
            assignmentDtos: new Dictionary<Guid, JsonObject>()));
        Assert.Equal($"DATA_INTEGRITY_ERROR: export DTO missing for assignment {Task:D}.", error.Message);
    }

    [Fact]
    public void RichQuizGraphExportAndImport_PreservesImageAndAnswerDocuments()
    {
        const string prompt = """{"type":"doc","content":[{"type":"image","attrs":{"src":"/api/files/editor-images%2Fdiagram.png","monochrome":true,"sourceBackground":"#000000","sourceDark":true}}]}""";
        const string option = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"mathInline","attrs":{"kind":"fraction","numerator":"1","denominator":"7"}}]}]}""";
        const string explanation = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Объяснение"}]},{"type":"image","attrs":{"src":"/api/files/editor-images%2Fexplanation.png"}}]}""";
        var questionId = Guid.NewGuid();
        var dto = new JsonObject
        {
            ["id"] = Task.ToString("D"), ["type"] = "test", ["title"] = "Quiz",
            ["description"] = prompt, ["tags"] = "", ["rating"] = 1, ["isVisible"] = true,
            ["testSettings"] = new JsonObject { ["passPercent"] = 75 },
            ["questions"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = questionId.ToString("D"), ["order"] = 0, ["type"] = "single-choice",
                    ["prompt"] = "Найдите дробь", ["promptContentJson"] = prompt, ["explanationContentJson"] = explanation,
                    ["promptEditorMode"] = "rich", ["optionsLayout"] = "inline",
                    ["options"] = new JsonArray
                    {
                        new JsonObject { ["key"] = "a", ["text"] = "1/7", ["contentJson"] = option, ["editorMode"] = "rich" },
                        new JsonObject { ["key"] = "b", ["text"] = "7", ["contentJson"] = prompt, ["editorMode"] = "rich" }
                    },
                    ["correctOptionKeys"] = new JsonArray(JsonValue.Create("a")),
                    ["acceptedAnswers"] = new JsonArray(), ["caseSensitive"] = false, ["trim"] = true
                }
            }
        };
        var tree = new CourseTreeResponse { CourseIds = [Root], Courses = [new CourseTreeCourseDto { Id = Root, Title = "Root" }] };
        var assignments = new[] { new Assignment { Id = Task, CourseId = Root, Title = "Quiz", Type = "test" } };
        var graph = BuildExport(Root, assignments, tree, new CourseMapInternalResponse(),
            new GraphExportOptions(IncludeGuide: false), assignmentDtos: new Dictionary<Guid, JsonObject> { [Task] = dto });
        var exported = Assert.Single(graph["tasks"]!.AsArray());
        var question = Assert.Single(exported!["questions"]!.AsArray());
        Assert.Equal(prompt, question!["promptContentJson"]!.GetValue<string>());
        Assert.Equal(explanation, question["explanationContentJson"]!.GetValue<string>());
        Assert.Equal("inline", question["optionsLayout"]!.GetValue<string>());
        Assert.Equal(option, question["options"]![0]!["contentJson"]!.GetValue<string>());
        Assert.Equal(prompt, question["options"]![1]!["contentJson"]!.GetValue<string>());
        Assert.Equal("a", question["correctOptionKeys"]![0]!.GetValue<string>());
        var parsed = ParseAndValidate(JsonSerializer.SerializeToElement(graph));
        Assert.Empty(parsed.Issues);
        var source = Assert.Single(parsed.Graph!.Tasks).Source;
        Assert.Equal(prompt, source.GetProperty("questions")[0].GetProperty("promptContentJson").GetString());
        Assert.Equal(explanation, source.GetProperty("questions")[0].GetProperty("explanationContentJson").GetString());
        Assert.Equal(option, source.GetProperty("questions")[0].GetProperty("options")[0].GetProperty("contentJson").GetString());

        var contentOnly = BuildExport(Root, assignments, tree, new CourseMapInternalResponse(),
            new GraphExportOptions(IncludeChecks: false, IncludeGuide: false),
            assignmentDtos: new Dictionary<Guid, JsonObject> { [Task] = dto });
        Assert.Null(contentOnly["tasks"]![0]!["questions"]);
    }

    private static (CourseTreeResponse Tree, Assignment[] Assignments) ExportSample()
    {
        var tree = new CourseTreeResponse { CourseIds = [Root, Sub], Courses = [
            new CourseTreeCourseDto { Id = Root, Title = "Root" },
            new CourseTreeCourseDto { Id = Sub, ParentCourseId = Root, Title = "Subcourse" }] };
        var assignments = new[] { new Assignment { Id = Task, CourseId = Root, Title = "Draft", Type = "code-test" } };
        return (tree, assignments);
    }
}
