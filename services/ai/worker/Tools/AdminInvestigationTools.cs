using System.ComponentModel;
using System.Text.Json.Nodes;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Tools;

public sealed class AdminInvestigationTools
{
    private readonly AgentRunContextAccessor _contextAccessor;

    public AdminInvestigationTools(AgentRunContextAccessor contextAccessor) => _contextAccessor = contextAccessor;

    [Description("Read a support ticket and immediately correlate it with the user's recent assignment activity, compact solution/attempt index, per-assignment summaries and an ordered investigation timeline. Admin-only. Use this first when the AI conversation was opened from support.")]
    public Task<JsonObject> InvestigateSupportTicketAsync(
        [Description("Support ticket id. Leave empty to use the supportTicketId attached to the current AI conversation.")] string? supportTicketId = null,
        [Description("Optional assignment id when the complaint is already known to concern one assignment.")] string? assignmentId = null,
        [Description("Hours of recent activity to correlate with the support chat.")] int hours = 24,
        [Description("Maximum compact events/solutions to return, 1..500.")] int take = 200)
        => RunAsync("investigate_support_ticket", new JsonObject
        {
            ["supportTicketId"] = CleanGuid(supportTicketId),
            ["assignmentId"] = CleanGuid(assignmentId),
            ["fromUtc"] = DateTimeOffset.UtcNow.AddHours(-System.Math.Clamp(hours, 1, 24 * 30)).ToString("O"),
            ["toUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["take"] = System.Math.Clamp(take, 1, 500)
        });

    [Description("Read the complete support chat attached to the current investigation, including participants and ordered messages. Admin-only.")]
    public Task<JsonObject> GetSupportChatAsync([Description("Support ticket id. Leave empty to use the current conversation supportTicketId.")] string? supportTicketId = null)
        => RunAsync("get_support_chat", new JsonObject { ["supportTicketId"] = CleanGuid(supportTicketId) });

    [Description("Read compact recent assignment activity for a user without opening pages in Browser API. Admin-only.")]
    public Task<JsonObject> GetUserRecentActivityAsync(string userId, int hours = 24, int take = 200)
        => RunAsync("get_user_recent_activity", WindowArgs(userId, null, hours, take));

    [Description("List a user's code, SQL, image, test and math submissions as one compact chronological index. Admin-only. Fetch details only for interesting items.")]
    public Task<JsonObject> ListUserSolutionsAsync(string userId, string? assignmentId = null, int hours = 168, int take = 200)
        => RunAsync("list_user_solutions", WindowArgs(userId, assignmentId, hours, take));

    [Description("Get full details for one code/sql/image/test/math solution or attempt after identifying it from the compact index. Admin-only.")]
    public Task<JsonObject> GetSolutionAsync(string kind, string itemId)
        => RunAsync("get_solution", new JsonObject { ["kind"] = kind, ["itemId"] = CleanGuid(itemId) });

    [Description("Get the complete assignment definition for investigation, including sensitive author-side checking data available to administrators, plus a direct assignment URL. Admin-only.")]
    public Task<JsonObject> GetAssignmentAsync(string assignmentId)
        => RunAsync("get_assignment", new JsonObject { ["assignmentId"] = CleanGuid(assignmentId) });

    private async Task<JsonObject> RunAsync(string action, JsonObject args)
    {
        RemoveNulls(args);
        var context = _contextAccessor.Current;
        return await context.Api.RunInvestigationAsync(context.Job.RunId, context.WorkerId, action, args, context.CancellationToken);
    }

    private static JsonObject WindowArgs(string userId, string? assignmentId, int hours, int take)
    {
        var now = DateTimeOffset.UtcNow;
        return new JsonObject
        {
            ["userId"] = CleanGuid(userId),
            ["assignmentId"] = CleanGuid(assignmentId),
            ["fromUtc"] = now.AddHours(-System.Math.Clamp(hours, 1, 24 * 30)).ToString("O"),
            ["toUtc"] = now.ToString("O"),
            ["take"] = System.Math.Clamp(take, 1, 500)
        };
    }

    private static string? CleanGuid(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString("D") : null;

    private static void RemoveNulls(JsonObject args)
    {
        foreach (var key in args.Where(x => x.Value is null).Select(x => x.Key).ToList())
            args.Remove(key);
    }
}
