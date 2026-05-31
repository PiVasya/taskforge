using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Prompts;
using TaskForge.AiAgent.Tools;

namespace TaskForge.AiAgent.Llm;

public sealed class TaskForgeAgentFactory
{
    private readonly ITaskForgeChatClientFactory _chatClientFactory;
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TaskForgeAgentOptions _options;

    public TaskForgeAgentFactory(
        ITaskForgeChatClientFactory chatClientFactory,
        IServiceProvider services,
        ILoggerFactory loggerFactory,
        IOptions<TaskForgeAgentOptions> options)
    {
        _chatClientFactory = chatClientFactory;
        _services = services;
        _loggerFactory = loggerFactory;
        _options = options.Value;
    }

    public AIAgent CreateCoordinatorAgent()
    {
        var chatClient = new ChatClientBuilder(_chatClientFactory.CreateChatClient())
            .UseFunctionInvocation()
            .Build();

        var courseTools = _services.GetRequiredService<CourseContextTools>();
        var draftTools = _services.GetRequiredService<AssignmentDraftTools>();
        var validationTools = _services.GetRequiredService<ValidationTools>();
        var approvalTools = _services.GetRequiredService<ApprovalTools>();
        var persistenceTools = _services.GetRequiredService<PersistenceTools>();

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(courseTools.GetCurrentRunContextAsync),
            AIFunctionFactory.Create(courseTools.SearchAssignmentsAsync),
            AIFunctionFactory.Create(courseTools.AnalyzeCourseGapAsync),
            AIFunctionFactory.Create(draftTools.BuildCodeAssignmentDraftAsync),
            AIFunctionFactory.Create(draftTools.BuildMathAssignmentDraftAsync),
            AIFunctionFactory.Create(draftTools.NormalizeDraftAsync),
            AIFunctionFactory.Create(validationTools.ValidateDraftShapeAsync),
            AIFunctionFactory.Create(validationTools.StaticDraftCritiqueAsync),
            AIFunctionFactory.Create(validationTools.RunCodeTestsAsync),
            AIFunctionFactory.Create(approvalTools.RequestHumanApprovalAsync)
        };

        if (_options.EnableDangerousWriteTools)
        {
            // Kept behind an explicit feature flag. Production flow should use approval artifacts instead.
            tools.Add(AIFunctionFactory.Create(persistenceTools.SaveHiddenDraftDirectAsync));
        }

        return new ChatClientAgent(
            chatClient,
            instructions: TaskForgeAgentPrompts.Coordinator,
            name: _options.AgentName,
            description: "TaskForge .NET coordinator agent with C# domain tools, workflow guards and HITL-safe write boundary.",
            tools: tools,
            loggerFactory: _loggerFactory,
            services: _services);
    }
}
