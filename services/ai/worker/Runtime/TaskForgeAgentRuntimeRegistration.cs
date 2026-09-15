using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskForge.AiAgent.Context;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Observability;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Safety;
using TaskForge.AiAgent.Tools;
using TaskForge.AiAgent.Workflows;
using TaskForge.AiAgent.Workflows.AgentLoop;
using TaskForge.AiAgent.Workflows.Executors;

namespace TaskForge.AiAgent.Runtime;

public static class TaskForgeAgentRuntimeRegistration
{
    public static IServiceCollection AddTaskForgeAgentRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TaskForgeAgentOptions>()
            .Bind(configuration.GetSection("TaskForgeAgent"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<AgentRunContextAccessor>();
        services.TryAddSingleton<AgentMemoryStore>();
        services.TryAddSingleton<PromptContextComposer>();
        services.TryAddSingleton<AgentStepReporter>();
        services.TryAddSingleton<AgentTelemetry>();
        services.TryAddSingleton<ResultEnvelopeBuilder>();
        services.TryAddSingleton<WriteOperationGuard>();

        services.AddHttpClient<DirectLlmTextClient>();
        services.TryAddSingleton<ITaskForgeChatClientFactory, OpenAiCompatibleChatClientFactory>();
        services.TryAddSingleton<AgentSessionStore>();
        services.TryAddSingleton<TaskForgeAgentFactory>();

        services.TryAddSingleton<CourseContextTools>();
        services.TryAddSingleton<AssignmentDraftTools>();
        services.TryAddSingleton<ValidationTools>();
        services.TryAddSingleton<ApprovalTools>();
        services.TryAddSingleton<PersistenceTools>();
        services.TryAddSingleton<AdminInvestigationTools>();

        services.TryAddSingleton<LoadRunContextExecutor>();
        services.TryAddSingleton<PlanRequestExecutor>();
        services.TryAddSingleton<TeacherPreferenceExecutor>();
        services.TryAddSingleton<CourseSkillMapExecutor>();
        services.TryAddSingleton<DraftAuthorExecutor>();
        services.TryAddSingleton<DraftValidationExecutor>();
        services.TryAddSingleton<DraftCriticExecutor>();
        services.TryAddSingleton<ApprovalGateExecutor>();
        services.TryAddSingleton<CourseAuditExecutor>();

        services.TryAddSingleton<AssignmentDraftWorkflow>();
        services.TryAddSingleton<CourseAuditWorkflow>();
        services.TryAddSingleton<CourseEditWorkflow>();
        services.TryAddSingleton<PolishAssignmentDraftWorkflow>();
        services.TryAddSingleton<OpenChatWorkflow>();
        services.TryAddSingleton<AgentLoopDecisionClient>();
        services.TryAddSingleton<AdaptiveAgentLoopWorkflow>();
        services.TryAddSingleton<TaskForgeWorkflowRouter>();
        services.TryAddSingleton<TaskForgeAgentRuntime>();
        return services;
    }
}
