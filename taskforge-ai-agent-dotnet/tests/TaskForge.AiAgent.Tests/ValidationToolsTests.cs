using System.Text.Json.Nodes;
using TaskForge.AiAgent.Runtime;
using TaskForge.AiAgent.Tools;
using Xunit;

namespace TaskForge.AiAgent.Tests;

public sealed class ValidationToolsTests
{
    [Fact]
    public void ValidateDraftShape_AllowsWhitespaceOnlyExpectedOutput()
    {
        var tools = new ValidationTools(new AgentRunContextAccessor());
        var draft = new JsonObject
        {
            ["assignmentType"] = "code-test",
            ["title"] = "Echo empty line",
            ["description"] = "Формат ввода: одна строка. Формат вывода: одна строка.",
            ["difficulty"] = 1,
            ["referenceSolution"] = "using System; class Program { static void Main() => Console.WriteLine(Console.ReadLine()); }",
            ["publicTests"] = new JsonArray
            {
                new JsonObject { ["input"] = "", ["expectedOutput"] = "\n", ["isHidden"] = false },
                new JsonObject { ["input"] = "abc", ["expectedOutput"] = "abc\n", ["isHidden"] = false }
            },
            ["hiddenTests"] = new JsonArray
            {
                new JsonObject { ["input"] = "x", ["expectedOutput"] = "x\n", ["isHidden"] = true },
                new JsonObject { ["input"] = " ", ["expectedOutput"] = " \n", ["isHidden"] = true }
            }
        };

        var result = tools.ValidateDraftShapeAsync(draft).GetAwaiter().GetResult();

        Assert.True(bool.TryParse(result["ok"]?.ToString(), out var ok) && ok);
    }

    [Fact]
    public void StaticDraftCritique_RecognizesRussianInputOutputWording()
    {
        var tools = new ValidationTools(new AgentRunContextAccessor());
        var draft = new JsonObject
        {
            ["assignmentType"] = "code-test",
            ["title"] = "Считываем строку",
            ["description"] = "Условие: считайте данные и выведите результат. Формат ввода: одна строка с числом. Формат вывода: одна строка с ответом. Используйте `Console.ReadLine()` и `Console.WriteLine(...)`."
        };

        var result = tools.StaticDraftCritiqueAsync(draft).GetAwaiter().GetResult();

        Assert.True(bool.TryParse(result["isAccepted"]?.ToString(), out var isAccepted) && isAccepted);
    }
    [Fact]
    public void StaticDraftCritique_RejectsServicePlacementText()
    {
        var tools = new ValidationTools(new AgentRunContextAccessor());
        var draft = new JsonObject
        {
            ["assignmentType"] = "code-test",
            ["title"] = "Подготовка к заданию 5 1. Считываем строку",
            ["description"] = "Место в курсе. Это подготовительное задание после List<T>: задача 4 и перед Задание 5.\n\nФормат ввода: одна строка. Формат вывода: одна строка. Используйте `Console.ReadLine()` и `Console.WriteLine(...)`."
        };

        var result = tools.StaticDraftCritiqueAsync(draft).GetAwaiter().GetResult();

        Assert.False(bool.TryParse(result["isAccepted"]?.ToString(), out var isAccepted) && isAccepted);
    }

    [Fact]
    public void StaticDraftCritique_RejectsUnwrappedCodeTokens()
    {
        var tools = new ValidationTools(new AgentRunContextAccessor());
        var draft = new JsonObject
        {
            ["assignmentType"] = "code-test",
            ["title"] = "Считываем строку",
            ["description"] = "Условие: считайте строку. Формат ввода: одна строка. Формат вывода: одна строка. Используйте Console.ReadLine() и Console.WriteLine(...)."
        };

        var result = tools.StaticDraftCritiqueAsync(draft).GetAwaiter().GetResult();

        Assert.False(bool.TryParse(result["isAccepted"]?.ToString(), out var isAccepted) && isAccepted);
    }

}
