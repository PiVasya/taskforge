using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;


namespace TaskForge.Tasks.Api.Contracts;

public sealed record ImageTestCaseSpec(string Name, string Input, string ExpectedOutput, string? ExpectedImageBase64, string? ExpectedImageKey, int Threshold, bool IsHidden, string ExpectedImageContentType, string ExpectedImageFileName)
{
    public string? ExpectedImageUrl => !string.IsNullOrWhiteSpace(ExpectedImageKey)
        ? $"/api/private-files/{Uri.EscapeDataString(ExpectedImageKey)}"
        : (!string.IsNullOrWhiteSpace(ExpectedImageBase64) ? $"data:{ExpectedImageContentType};base64,{ExpectedImageBase64}" : null);
}

public sealed record TestAnswer(Guid QuestionId, string? SelectedOptionKey, List<string>? SelectedOptionKeys, string? Text);

public sealed record MathAnswer(Guid BlockId, string? Text, List<string>? SelectedOptionKeys, List<string>? OrderedItems, List<MatchPair>? MatchPairs);

public sealed record MatchPair(string LeftKey, string RightKey);

public sealed record TestQuestion(Guid Id, int Order, string Type, string Prompt, List<Option> Options, List<string> CorrectOptionKeys, List<string> AcceptedAnswers, bool CaseSensitive, bool Trim);

public sealed record Option(string Key, string Text);

public sealed record TaskSpec(TestSettings Settings, List<TestQuestion> Questions);

public sealed record MathBlock(Guid Id, int Order, string Kind, string Prompt, string? PromptContentJson, int Score, bool IsRequired, List<Option> Options, List<string> CorrectOptionKeys, List<string> AcceptedAnswers, bool CaseSensitive, bool Trim, double? NumericTolerance, List<string> OrderItems, List<Option> MatchLeftItems, List<Option> MatchRightItems, List<MatchPair> MatchPairs);

public sealed record MathSpec(MathSettings Settings, List<MathBlock> Blocks);
