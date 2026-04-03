using taskforge.Data.Models.Entities.AI;

namespace taskforge.Services.AI;

public sealed partial class AiJobService
{
    private static string PreviewForConsole(string? value, int limit = 180)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var clean = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length <= limit ? clean : clean[..(limit - 3)] + "...";
    }

    private static string DescribeStage(string? stageCode, string? stageLabel, int? stageOrder)
        => $"stageCode='{stageCode}' stageLabel='{PreviewForConsole(stageLabel, 80)}' stageOrder={stageOrder}";

    private static string DescribeJobForConsole(AiJob job)
        => $"jobId={job.Id} type='{job.Type}' status='{job.Status}' priority={job.Priority} {DescribeStage(job.StageCode, job.StageLabel, job.StageOrder)} targetType='{job.TargetEntityType}' targetId='{job.TargetEntityId}' courseId='{job.CourseId}' parentJobId='{job.ParentJobId}'";

    private static string DescribeBatchForConsole(AiBatch? batch)
        => batch == null
            ? "batch=<null>"
            : $"batchId={batch.Id} status='{batch.Status}' currentStage='{batch.CurrentStage}' requestedCount={batch.RequestedCount} items={batch.Items?.Count ?? 0} courseId='{batch.CourseId}' assignmentType='{batch.AssignmentType}' mode='{batch.Mode}' prompt='{PreviewForConsole(batch.Prompt, 120)}'";

    private static string DescribeBatchItemForConsole(AiBatchItem? item)
        => item == null
            ? "item=<null>"
            : $"itemId={item.Id} batchId='{item.BatchId}' index={item.Index} status='{item.Status}' difficulty={item.DifficultyTarget} targetSkill='{PreviewForConsole(item.TargetSkill, 80)}' microGoal='{PreviewForConsole(item.MicroGoal, 120)}' draftId='{item.DraftId}' repairCount={item.RepairCount}";

    private static string DescribeDraftForConsole(AiGeneratedAssignmentDraft? draft)
        => draft == null
            ? "draft=<null>"
            : $"draftId={draft.Id} jobId='{draft.JobId}' status='{draft.Status}' courseId='{draft.CourseId}' batchId='{draft.BatchId}' batchItemId='{draft.BatchItemId}' assignmentType='{draft.AssignmentType}' title='{PreviewForConsole(draft.Title, 120)}'";

    private static void ConsoleFoundry(string action, AiJob job, string? extra = null)
    {
        Console.WriteLine($"[AiJobService][Foundry] {action} {DescribeJobForConsole(job)}{(string.IsNullOrWhiteSpace(extra) ? string.Empty : " " + extra)}");
    }

    private static void ConsoleFoundryBatch(string action, AiBatch? batch, string? extra = null)
    {
        Console.WriteLine($"[AiJobService][Foundry] {action} {DescribeBatchForConsole(batch)}{(string.IsNullOrWhiteSpace(extra) ? string.Empty : " " + extra)}");
    }

    private static void ConsoleFoundryItem(string action, AiBatchItem? item, string? extra = null)
    {
        Console.WriteLine($"[AiJobService][Foundry] {action} {DescribeBatchItemForConsole(item)}{(string.IsNullOrWhiteSpace(extra) ? string.Empty : " " + extra)}");
    }

    private static void ConsoleFoundryDraft(string action, AiGeneratedAssignmentDraft? draft, string? extra = null)
    {
        Console.WriteLine($"[AiJobService][Foundry] {action} {DescribeDraftForConsole(draft)}{(string.IsNullOrWhiteSpace(extra) ? string.Empty : " " + extra)}");
    }
}
