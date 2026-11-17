namespace TaskForge.Ai.Api.Endpoints;

internal static partial class AiApiEndpoints
{
    internal static WebApplication MapAiApiEndpoints(this WebApplication app)
    {
        MapServiceInfoEndpoints(app);
        MapAccountIntelligenceEndpoints(app);
        MapAccountLifecycleEndpoints(app);
        return app;
    }
}
