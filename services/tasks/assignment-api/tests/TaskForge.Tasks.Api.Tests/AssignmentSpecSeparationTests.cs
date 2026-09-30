using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Mapping;
using TaskForge.Tasks.Api.Services.Serialization;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class AssignmentSpecSeparationTests
{
    [Fact]
    public void Model_UsesDedicatedTablesForEveryNonSqlAssignmentKind()
    {
        using var db = new TasksDbContextFactory().CreateDbContext([]);

        Assert.Equal("CodeAssignmentSpecs", db.Model.FindEntityType(typeof(CodeAssignmentSpec))?.GetTableName());
        Assert.Equal("ImageAssignmentSpecs", db.Model.FindEntityType(typeof(ImageAssignmentSpec))?.GetTableName());
        Assert.Equal("MathAssignmentSpecs", db.Model.FindEntityType(typeof(MathAssignmentSpec))?.GetTableName());
        Assert.Equal("TestAssignmentSpecs", db.Model.FindEntityType(typeof(TestAssignmentSpec))?.GetTableName());

        foreach (var type in new[] { typeof(CodeAssignmentSpec), typeof(ImageAssignmentSpec), typeof(MathAssignmentSpec), typeof(TestAssignmentSpec) })
        {
            var entity = db.Model.FindEntityType(type);
            Assert.NotNull(entity);
            var foreignKey = Assert.Single(entity!.GetForeignKeys());
            Assert.Equal(typeof(Assignment), foreignKey.PrincipalEntityType.ClrType);
            Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        }
    }

    [Fact]
    public void MathDto_DoesNotExposeExecutableAssignmentFields()
    {
        var assignment = new Assignment
        {
            Id = Guid.NewGuid(),
            CourseId = Guid.NewGuid(),
            Type = "math",
            Title = "Математика",
            Language = "csharp",
            AllowedLanguagesCsv = "csharp,cpp",
            StarterCode = "legacy",
            CodeForbiddenCallsJson = "[\"exec\"]",
            CodeRequiredCallsJson = "[\"main\"]",
            TestsJson = "{\"settings\":{\"passPercent\":80},\"blocks\":[]}"
        };

        var dto = AssignmentApiMappingService.ToDto(assignment, includeSensitive: true);
        var node = JsonSerializer.SerializeToNode(dto, AssignmentApiSerializationService.JsonOptions())!.AsObject();

        Assert.Equal(string.Empty, node["language"]?.GetValue<string>());
        Assert.Empty(node["allowedLanguages"]!.AsArray());
        Assert.Null(node["starterCode"]);
        Assert.Empty(node["codeForbiddenCalls"]!.AsArray());
        Assert.Empty(node["codeRequiredCalls"]!.AsArray());
        Assert.NotNull(node["tests"]);
        Assert.Null(node["imageTestSimilarityThreshold"]);
    }

    [Fact]
    public void MathExport_OmitsExecutableFields()
    {
        var assignment = new Assignment
        {
            Id = Guid.NewGuid(),
            CourseId = Guid.NewGuid(),
            Type = "math",
            Title = "Математика",
            Language = "csharp",
            AllowedLanguagesCsv = "csharp",
            StarterCode = "legacy",
            TestsJson = "{\"settings\":{\"passPercent\":80},\"blocks\":[]}"
        };

        var exported = AssignmentApiSerializationService.ToImportDto(assignment);

        Assert.False(exported.ContainsKey("language"));
        Assert.False(exported.ContainsKey("allowedLanguages"));
        Assert.False(exported.ContainsKey("starterCode"));
        Assert.True(exported.ContainsKey("testSettings"));
        Assert.True(exported.ContainsKey("blocks"));
    }
}
