using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Context;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public sealed partial class AdaptiveAgentLoopWorkflow
{
    private async Task<JsonObject> ExecuteActionAsync(AgentLoopState state, string action, JsonObject args, CancellationToken cancellationToken)
    {
        if (state.DelegatedResult is not null && action.StartsWith("delegate_", StringComparison.OrdinalIgnoreCase))
        {
            state.Notes.Add($"Delegation '{action}' was ignored: a workflow result already exists.");
            return new JsonObject
            {
                ["ok"] = false,
                ["message"] = "Результат рабочего сценария уже получен. Следующий шаг должен проверить итог и завершить run."
            };
        }

        if (action.StartsWith("delegate_", StringComparison.OrdinalIgnoreCase))
        {
            if (!state.LoadedContext)
            {
                state.Notes.Add($"Delegation '{action}' was delayed: context has not been inspected yet.");
                return new JsonObject
                {
                    ["ok"] = false,
                    ["message"] = "Сначала нужно выполнить inspect_context, чтобы не потерять контекст чата и курса."
                };
            }

            if (!state.WorkingMemory.ContainsKey("intent"))
            {
                state.Notes.Add($"Delegation '{action}' was delayed: request has not been classified yet.");
                return new JsonObject
                {
                    ["ok"] = false,
                    ["message"] = "Сначала нужно выполнить classify_request, чтобы выбрать сценарий осознанно."
                };
            }
        }

        switch (action)
        {
            case "inspect_context":
                return InspectContext(state);
            case "classify_request":
                return ClassifyRequest(state);
            case "load_editable_assignments":
                return CourseAgentTools.LoadEditableAssignments(state);
            case "map_course_structure":
                return CourseAgentTools.MapCourseStructure(state);
            case "extract_course_style":
                return CourseAgentTools.ExtractCourseStyle(state);
            case "find_learning_gaps":
                return CourseAgentTools.FindLearningGaps(state);
            case "analyze_assignment_complexity":
                return CourseAgentTools.AnalyzeAssignmentComplexity(state);
            case "plan_course_enrichment":
                return CourseAgentTools.PlanCourseEnrichment(state);
            case "search_course":
                return CourseAgentTools.SearchCourse(state, args);
            case "propose_assignment_patch_set":
                return CourseAgentTools.ProposeAssignmentPatchSet(state, args, _options.MaxPatchOperationsPerRun);
            case "review_patch_set":
                return CourseAgentTools.ReviewPatchSet(state);
            case "review_delegated_result":
                return CourseAgentTools.ReviewDelegatedResult(state);
            case "delegate_assignment_draft":
                return await DelegateWorkflowAsync(state, _assignmentDraft, "assignment_draft_workflow", cancellationToken);
            case "delegate_course_audit":
                return await DelegateWorkflowAsync(state, _courseAudit, "course_audit_workflow", cancellationToken);
            case "delegate_course_edit":
                return await DelegateWorkflowAsync(state, _courseEdit, "course_edit_workflow", cancellationToken);
            case "delegate_polish_assignment":
                return await DelegateWorkflowAsync(state, _polish, "polish_assignment_draft", cancellationToken);
            case "answer_directly":
                return await AnswerDirectlyAsync(state, cancellationToken);
            case "finish":
                var hasPatchSet = state.WorkingMemory.ContainsKey("pendingPatchSet");
                if (state.DelegatedResult is null && !hasPatchSet && string.IsNullOrWhiteSpace(state.FinalMessage))
                {
                    state.Notes.Add("Model tried to finish before producing a result; finish was ignored.");
                    return new JsonObject
                    {
                        ["ok"] = false,
                        ["finished"] = false,
                        ["message"] = "Нельзя завершить run без результата. Нужно выбрать рабочий шаг."
                    };
                }
                if (hasPatchSet && !state.WorkingMemory.ContainsKey("patchSetReview"))
                {
                    state.Notes.Add("Finish was delayed: pending patch set must be reviewed first.");
                    return new JsonObject
                    {
                        ["ok"] = false,
                        ["finished"] = false,
                        ["message"] = "Сначала выполни review_patch_set, чтобы не завершать run без проверки патчей."
                    };
                }
                if (state.DelegatedResult is not null && !state.WorkingMemory.ContainsKey("delegatedResultReview"))
                {
                    state.Notes.Add("Finish was delayed: delegated workflow result must be reviewed first.");
                    return new JsonObject
                    {
                        ["ok"] = false,
                        ["finished"] = false,
                        ["message"] = "Сначала выполни review_delegated_result, чтобы не завершать run без проверки результата."
                    };
                }
                state.Finished = true;
                return new JsonObject
                {
                    ["ok"] = true,
                    ["finished"] = true,
                    ["hasDelegatedResult"] = state.DelegatedResult is not null,
                    ["hasPatchSet"] = state.WorkingMemory.ContainsKey("pendingPatchSet"),
                    ["hasFinalMessage"] = !string.IsNullOrWhiteSpace(state.FinalMessage)
                };
            default:
                return new JsonObject
                {
                    ["ok"] = false,
                    ["message"] = $"Unknown action: {action}"
                };
        }
    }

}
