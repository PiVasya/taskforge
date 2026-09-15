using System.Text.Json.Nodes;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public sealed partial class AdaptiveAgentLoopWorkflow
{
    private async Task<JsonObject> ExecuteInvestigationActionAsync(
        AgentLoopState state,
        string action,
        JsonObject args,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JsonObject result;
        switch (action)
        {
            case "investigate_support_ticket":
                result = await _investigationTools.InvestigateSupportTicketAsync(
                    StringArg(args, "supportTicketId") ?? state.WorkingMemory["supportTicketId"]?.ToString(),
                    StringArg(args, "assignmentId") ?? state.Job.AssignmentId?.ToString(),
                    IntArg(args, "hours", 24),
                    IntArg(args, "take", 200));
                state.WorkingMemory["supportInvestigation"] = result.DeepClone();
                if (!string.IsNullOrWhiteSpace(result["userId"]?.ToString()))
                    state.WorkingMemory["userId"] = result["userId"]!.DeepClone();
                break;
            case "get_support_chat":
                result = await _investigationTools.GetSupportChatAsync(
                    StringArg(args, "supportTicketId") ?? state.WorkingMemory["supportTicketId"]?.ToString());
                state.WorkingMemory["supportChat"] = result.DeepClone();
                break;
            case "get_user_recent_activity":
                result = await _investigationTools.GetUserRecentActivityAsync(
                    RequireArg(args, state, "userId"),
                    IntArg(args, "hours", 24),
                    IntArg(args, "take", 200));
                state.WorkingMemory["userActivity"] = result.DeepClone();
                break;
            case "list_user_solutions":
                result = await _investigationTools.ListUserSolutionsAsync(
                    RequireArg(args, state, "userId"),
                    StringArg(args, "assignmentId"),
                    IntArg(args, "hours", 168),
                    IntArg(args, "take", 200));
                state.WorkingMemory["solutionIndex"] = result.DeepClone();
                break;
            case "get_solution":
                result = await _investigationTools.GetSolutionAsync(
                    RequireArg(args, state, "kind"),
                    RequireArg(args, state, "itemId"));
                state.WorkingMemory["solutionDetail"] = result.DeepClone();
                break;
            case "get_assignment":
                result = await _investigationTools.GetAssignmentAsync(
                    StringArg(args, "assignmentId") ?? state.Job.AssignmentId?.ToString()
                    ?? throw new InvalidOperationException("get_assignment requires assignmentId."));
                state.WorkingMemory["assignmentDetail"] = result.DeepClone();
                break;
            default:
                throw new InvalidOperationException($"Unknown investigation action: {action}");
        }

        state.ScenarioId = "support_investigation";
        return new JsonObject
        {
            ["ok"] = true,
            ["action"] = action,
            ["summary"] = action switch
            {
                "investigate_support_ticket" => "Обращение связано с недавней активностью и компактным индексом решений пользователя.",
                "get_support_chat" => "Чат поддержки загружен напрямую через read-only API.",
                "get_user_recent_activity" => "Недавняя активность пользователя загружена напрямую через read-only API.",
                "list_user_solutions" => "Компактный индекс решений и попыток загружен без открытия карточек в интерфейсе.",
                "get_solution" => "Подробности выбранного решения загружены.",
                "get_assignment" => "Полное задание загружено напрямую через read-only API.",
                _ => "Данные расследования загружены."
            },
            ["result"] = result.DeepClone()
        };
    }

    private static string? StringArg(JsonObject args, string name)
        => string.IsNullOrWhiteSpace(args[name]?.ToString()) ? null : args[name]!.ToString();

    private static int IntArg(JsonObject args, string name, int fallback)
        => int.TryParse(args[name]?.ToString(), out var value) ? value : fallback;

    private static string RequireArg(JsonObject args, AgentLoopState state, string name)
    {
        var value = StringArg(args, name);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (name == "userId" && !string.IsNullOrWhiteSpace(state.WorkingMemory["userId"]?.ToString()))
            return state.WorkingMemory["userId"]!.ToString();
        throw new InvalidOperationException($"{name} is required for this investigation action.");
    }
}
