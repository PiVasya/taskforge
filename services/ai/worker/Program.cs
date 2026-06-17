using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskForge.AiAgent.Context;
using TaskForge.AiAgent.Infrastructure;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Observability;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Runtime;
using TaskForge.AiAgent.Safety;
using TaskForge.AiAgent.Tools;
using TaskForge.AiAgent.Workflows;
using TaskForge.AiAgent.Workflows.AgentLoop;
using TaskForge.AiAgent.Workflows.Executors;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("ai-worker");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "ai-worker");

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddOptions<TaskForgeAgentOptions>()
    .Bind(builder.Configuration.GetSection("TaskForgeAgent"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<TaskForgeInternalApiOptions>()
    .Bind(builder.Configuration.GetSection("TaskForgeInternalApi"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient<TaskForgeInternalApiClient>();

builder.Services.AddSingleton<AgentRunContextAccessor>();
builder.Services.AddSingleton<AgentStepReporter>();
builder.Services.AddSingleton<AgentSessionStore>();
builder.Services.AddSingleton<AgentTelemetry>();
builder.Services.AddSingleton<WriteOperationGuard>();
builder.Services.AddSingleton<ResultEnvelopeBuilder>();
builder.Services.AddSingleton<PromptContextComposer>();
builder.Services.AddSingleton<AgentMemoryStore>();

builder.Services.AddSingleton<ITaskForgeChatClientFactory, OpenAiCompatibleChatClientFactory>();
builder.Services.AddHttpClient<DirectLlmTextClient>();
builder.Services.AddSingleton<TaskForgeAgentFactory>();

builder.Services.AddTransient<CourseContextTools>();
builder.Services.AddTransient<AssignmentDraftTools>();
builder.Services.AddTransient<ValidationTools>();
builder.Services.AddTransient<ApprovalTools>();
builder.Services.AddTransient<PersistenceTools>();

builder.Services.AddTransient<LoadRunContextExecutor>();
builder.Services.AddTransient<PlanRequestExecutor>();
builder.Services.AddTransient<TeacherPreferenceExecutor>();
builder.Services.AddTransient<CourseSkillMapExecutor>();
builder.Services.AddTransient<DraftAuthorExecutor>();
builder.Services.AddTransient<DraftCriticExecutor>();
builder.Services.AddTransient<DraftValidationExecutor>();
builder.Services.AddTransient<ApprovalGateExecutor>();
builder.Services.AddTransient<CourseAuditExecutor>();
builder.Services.AddTransient<AgentLoopDecisionClient>();

builder.Services.AddTransient<OpenChatWorkflow>();
builder.Services.AddTransient<CourseAuditWorkflow>();
builder.Services.AddTransient<AssignmentDraftWorkflow>();
builder.Services.AddTransient<PolishAssignmentDraftWorkflow>();
builder.Services.AddTransient<CourseEditWorkflow>();
builder.Services.AddTransient<AdaptiveAgentLoopWorkflow>();
builder.Services.AddSingleton<TaskForgeWorkflowRouter>();

builder.Services.AddSingleton<TaskForgeAgentRuntime>();
builder.Services.AddHostedService<TaskForgeAgentWorker>();

await builder.Build().RunAsync();
