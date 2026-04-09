using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using taskforge.Constants;
using taskforge.Data.Models.DTO.AI;
using taskforge.Data.Models.Entities.AI;

namespace taskforge.Services.AI;

public sealed partial class AiJobService
{
    public async Task<AiBatchDetailsDto> QueueGenerateAssignmentBatchAsync(AiGenerateAssignmentBatchRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default)
    {
        Console.WriteLine($"[AiJobService][Foundry] batch-queue >>> courseId='{request.CourseId}' assignmentType='{request.AssignmentType}' mode='{request.Mode}' count={request.Count} difficulty={request.Difficulty} prompt='{PreviewForConsole(request.Prompt, 160)}'");
        var batch = new AiBatch
        {
            Id = Guid.NewGuid(),
            CourseId = request.CourseId,
            CreatedByUserId = createdByUserId,
            Prompt = request.Prompt,
            AssignmentType = NormalizeDraftAssignmentType(request.AssignmentType, default),
            Mode = string.IsNullOrWhiteSpace(request.Mode) ? "topic-pack" : request.Mode.Trim(),
            RequestedCount = Math.Clamp(request.Count, 1, 50),
            Status = "planning",
            CurrentStage = AiFoundryStages.BatchPlan,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        _db.AiBatches.Add(batch);
        await _db.SaveChangesAsync(ct);
        ConsoleFoundryBatch("batch-created", batch);

        var referenceAssignments = await BuildReferenceAssignmentsAsync(batch.CourseId, batch.AssignmentType, ct);
        var supportedLanguages = BuildSupportedLanguages(batch.AssignmentType, referenceAssignments, batch.Prompt);
        var input = new
        {
            requestType = "assignment_batch_generate",
            batchId = batch.Id,
            courseId = batch.CourseId,
            assignmentType = batch.AssignmentType,
            prompt = request.Prompt,
            difficulty = request.Difficulty,
            count = batch.RequestedCount,
            mode = batch.Mode,
            notes = request.Notes,
            referenceAssignments,
            supportedLanguages,
            allowedLanguages = supportedLanguages,
            targetSchema = BuildTargetSchema(batch.AssignmentType),
            qualityGates = BuildQualityGates(batch.AssignmentType),
        };

        ConsoleFoundryBatch("batch-enqueue-batch-plan", batch, $"priority={request.Priority} stageCode='{AiFoundryStages.BatchPlan}'");

        await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = AiFoundryJobTypes.BatchPlan,
            TargetEntityType = "ai-batch",
            TargetEntityId = batch.Id,
            CourseId = batch.CourseId,
            Priority = request.Priority,
            StageCode = AiFoundryStages.BatchPlan,
            StageLabel = "AI batch planning",
            StageOrder = 10,
            InputJson = JsonSerializer.Serialize(input, JsonOptions),
        }, createdByUserId, createdByDisplayName, ct);

        ConsoleFoundryBatch("batch-queue-finish", batch);
        return await GetBatchAsync(batch.Id, ct) ?? throw new InvalidOperationException("Batch was created but could not be loaded.");
    }

    public async Task<IReadOnlyList<AiBatchListItemDto>> GetBatchesAsync(CancellationToken ct = default)
    {
        return await _db.AiBatches.AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => new AiBatchListItemDto
            {
                Id = x.Id,
                CourseId = x.CourseId,
                AssignmentType = x.AssignmentType,
                Mode = x.Mode,
                RequestedCount = x.RequestedCount,
                Status = x.Status,
                CurrentStage = x.CurrentStage,
                Prompt = x.Prompt,
                CreatedAtUtc = x.CreatedAtUtc,
                UpdatedAtUtc = x.UpdatedAtUtc,
                ItemsCount = x.Items.Count,
                ReadyItemsCount = x.Items.Count(i => i.Status == "ready" || i.Status == "reviewed"),
            })
            .ToListAsync(ct);
    }

    public async Task<AiBatchDetailsDto?> GetBatchAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.AiBatches.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new AiBatchDetailsDto
            {
                Id = x.Id,
                CourseId = x.CourseId,
                AssignmentType = x.AssignmentType,
                Mode = x.Mode,
                RequestedCount = x.RequestedCount,
                Status = x.Status,
                CurrentStage = x.CurrentStage,
                Prompt = x.Prompt,
                CreatedAtUtc = x.CreatedAtUtc,
                UpdatedAtUtc = x.UpdatedAtUtc,
                ItemsCount = x.Items.Count,
                ReadyItemsCount = x.Items.Count(i => i.Status == "ready" || i.Status == "reviewed"),
                CanonicalRequestJson = x.CanonicalRequestJson,
                CourseProfileJson = x.CourseProfileJson,
                GapAnalysisJson = x.GapAnalysisJson,
                AssignmentOntologyJson = x.AssignmentOntologyJson,
                ExemplarSignalsJson = x.ExemplarSignalsJson,
                NegativeMemoryJson = x.NegativeMemoryJson,
                CoverageJson = x.CoverageJson,
                PlanJson = x.PlanJson,
                SummaryJson = x.SummaryJson,
                DecisionSummaryJson = x.DecisionSummaryJson,
                BatchReviewJson = x.BatchReviewJson,
                ReviewLedgerJson = x.ReviewLedgerJson,
                StudentJourneyJson = x.StudentJourneyJson,
                PublicationAuditJson = x.PublicationAuditJson,
                PublishPackJson = x.PublishPackJson,
                QualityLedgerJson = x.QualityLedgerJson,
                ExportManifestJson = x.ExportManifestJson,
                PlannerFeedbackJson = x.PlannerFeedbackJson,
                HistoricalPlannerPriorsJson = x.HistoricalPlannerPriorsJson,
                PositiveMemoryJson = x.PositiveMemoryJson,
                BatchMemoryJson = x.BatchMemoryJson,
                InstitutionalMemoryJson = x.InstitutionalMemoryJson,
                AntiPatternMemoryJson = x.AntiPatternMemoryJson,
                ReplanLedgerJson = x.ReplanLedgerJson,
                DecisionLogDigestJson = x.DecisionLogDigestJson,
                FeedbackLoopStateJson = x.FeedbackLoopStateJson,
                Items = x.Items.OrderBy(i => i.Index).Select(i => new AiBatchItemDto
                {
                    Id = i.Id,
                    Index = i.Index,
                    TargetSkill = i.TargetSkill,
                    DifficultyTarget = i.DifficultyTarget,
                    MicroGoal = i.MicroGoal,
                    Status = i.Status,
                    DraftId = i.DraftId,
                    RepairCount = i.RepairCount,
                    BriefJson = i.BriefJson,
                    BriefReviewJson = i.BriefReviewJson,
                    ContextReviewJson = i.ContextReviewJson,
                    DecisionLogJson = i.DecisionLogJson,
                    PlannerSignalsJson = i.PlannerSignalsJson,
                    HistoricalSlotPriorsJson = i.HistoricalSlotPriorsJson,
                    AntiPatternFlagsJson = i.AntiPatternFlagsJson,
                    ReplanHistoryJson = i.ReplanHistoryJson,
                    ReferencePackJson = i.ReferencePackJson,
                    StylePackJson = i.StylePackJson,
                    PolicyPackJson = i.PolicyPackJson,
                    NegativePackJson = i.NegativePackJson,
                    ExemplarPackJson = i.ExemplarPackJson,
                    ReferenceSignalsJson = i.ReferenceSignalsJson,
                    ScorecardJson = i.ScorecardJson,
                }).ToList(),
            })
            .FirstOrDefaultAsync(ct);
    }

    private async Task PersistCourseProfileAndEnqueueGapAnalysisAsync(AiJob completedJob, CancellationToken ct)
    {
        ConsoleFoundry("course-profile-persist-start", completedJob);
        if (completedJob.TargetEntityId == null || string.IsNullOrWhiteSpace(completedJob.ResultJson))
            return;

        var batch = await _db.AiBatches.FirstOrDefaultAsync(x => x.Id == completedJob.TargetEntityId.Value, ct);
        if (batch == null)
            return;

        batch.CourseProfileJson = completedJob.ResultJson;
        TryPopulateBatchSignalsFromCourseProfile(batch, completedJob.ResultJson);
        batch.Status = "profiling";
        batch.CurrentStage = AiFoundryStages.GapAnalysis;
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await CreateBatchDecisionLogAsync(batch.Id, null, completedJob.Id, AiFoundryStages.CourseProfileBuild, "course-profile-ready", "Профиль курса построен и сохранён в batch.", completedJob.ResultJson, ct);
        await PersistReferenceSnapshotsAsync(batch.Id, null, completedJob.Id, "course-profile-input", completedJob.InputJson, ct);

        var referenceAssignments = await BuildReferenceAssignmentsAsync(batch.CourseId, batch.AssignmentType, ct);

        await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = AiFoundryJobTypes.GapAnalysis,
            ParentJobId = completedJob.Id,
            TargetEntityType = "ai-batch",
            TargetEntityId = batch.Id,
            CourseId = batch.CourseId,
            Priority = Math.Max(completedJob.Priority - 1, 1),
            StageCode = AiFoundryStages.GapAnalysis,
            StageLabel = "AI gap analysis",
            StageOrder = 8,
            InputJson = JsonSerializer.Serialize(new
            {
                requestType = "assignment_gap_analysis",
                batchId = batch.Id,
                courseId = batch.CourseId,
                assignmentType = batch.AssignmentType,
                prompt = batch.Prompt,
                mode = batch.Mode,
                count = batch.RequestedCount,
                courseProfile = JsonSerializer.Deserialize<object>(batch.CourseProfileJson ?? "{}"),
                referenceAssignments = referenceAssignments,
            }, JsonOptions),
        }, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
    }

    private async Task PersistGapAnalysisAndEnqueueBatchPlanAsync(AiJob completedJob, CancellationToken ct)
    {
        ConsoleFoundry("gap-analysis-persist-start", completedJob);
        if (completedJob.TargetEntityId == null || string.IsNullOrWhiteSpace(completedJob.ResultJson))
            return;

        var batch = await _db.AiBatches.FirstOrDefaultAsync(x => x.Id == completedJob.TargetEntityId.Value, ct);
        if (batch == null)
            return;

        using var gapDoc = JsonDocument.Parse(completedJob.ResultJson);
        if (!HasGapAnalysisShape(gapDoc.RootElement))
        {
            batch.GapAnalysisJson = completedJob.ResultJson;
            batch.Status = "gap-analysis-invalid-result";
            batch.CurrentStage = AiFoundryStages.GapAnalysis;
            batch.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await CreateBatchDecisionLogAsync(batch.Id, null, completedJob.Id, AiFoundryStages.GapAnalysis, "gap-analysis-invalid", "Gap analysis не содержит обязательные ключи gapAnalysis/coverage. Planner не поставлен.", completedJob.ResultJson, ct);
            return;
        }

        batch.GapAnalysisJson = completedJob.ResultJson;
        TryPopulateBatchSignalsFromGapAnalysis(batch, completedJob.ResultJson);
        batch.Status = "gap-analysis";
        batch.CurrentStage = AiFoundryStages.BatchPlan;
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await CreateBatchDecisionLogAsync(batch.Id, null, completedJob.Id, AiFoundryStages.GapAnalysis, "gap-analysis-ready", "Gap analysis завершён и передан в planner.", completedJob.ResultJson, ct);
        await PersistReferenceSnapshotsAsync(batch.Id, null, completedJob.Id, "gap-analysis-input", completedJob.InputJson, ct);

        var historicalPlannerPriors = await BuildHistoricalPlannerPriorsAsync(batch.CourseId, batch.AssignmentType, batch.Id, ct);
        batch.HistoricalPlannerPriorsJson = historicalPlannerPriors;
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var referenceAssignments = await BuildReferenceAssignmentsAsync(batch.CourseId, batch.AssignmentType, ct);

        await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = AiFoundryJobTypes.BatchPlan,
            ParentJobId = completedJob.Id,
            TargetEntityType = "ai-batch",
            TargetEntityId = batch.Id,
            CourseId = batch.CourseId,
            Priority = Math.Max(completedJob.Priority - 1, 1),
            StageCode = AiFoundryStages.BatchPlan,
            StageLabel = "AI batch planning",
            StageOrder = 10,
            InputJson = JsonSerializer.Serialize(new
            {
                requestType = "assignment_batch_generate",
                batchId = batch.Id,
                courseId = batch.CourseId,
                assignmentType = batch.AssignmentType,
                prompt = batch.Prompt,
                count = batch.RequestedCount,
                mode = batch.Mode,
                courseProfile = JsonSerializer.Deserialize<object>(batch.CourseProfileJson ?? "{}"),
                gapAnalysis = JsonSerializer.Deserialize<object>(batch.GapAnalysisJson ?? "{}"),
                historicalPlannerPriors = JsonSerializer.Deserialize<object>(batch.HistoricalPlannerPriorsJson ?? "{}"),
                referenceAssignments = referenceAssignments,
                targetSchema = BuildTargetSchema(batch.AssignmentType),
                qualityGates = BuildQualityGates(batch.AssignmentType),
            }, JsonOptions),
        }, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
    }

private static List<JsonElement> ExtractPlannerTasks(JsonElement root)
{
    var tasks = new List<JsonElement>();
    if (root.ValueKind != JsonValueKind.Object)
        return tasks;

    if (root.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object && plan.TryGetProperty("tasks", out var planTasks) && planTasks.ValueKind == JsonValueKind.Array)
        tasks.AddRange(planTasks.EnumerateArray());
    if (tasks.Count == 0 && root.TryGetProperty("tasks", out var directTasks) && directTasks.ValueKind == JsonValueKind.Array)
        tasks.AddRange(directTasks.EnumerateArray());
    if (tasks.Count == 0 && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        tasks.AddRange(items.EnumerateArray());
    if (tasks.Count == 0 && root.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
        tasks.AddRange(slots.EnumerateArray());

    return tasks;
}

private static bool HasGapAnalysisShape(JsonElement root)
{
    return root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty("gapAnalysis", out var gap)
           && gap.ValueKind == JsonValueKind.Object
           && root.TryGetProperty("coverage", out var coverage)
           && coverage.ValueKind == JsonValueKind.Object;
}


private async Task PersistBatchPlanAsync(AiJob completedJob, bool isReplan, CancellationToken ct)
{
    ConsoleFoundry(isReplan ? "batch-replan-persist-start" : "batch-plan-persist-start", completedJob);
    if (completedJob.TargetEntityId == null || string.IsNullOrWhiteSpace(completedJob.ResultJson))
        return;

    var batch = await _db.AiBatches.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == completedJob.TargetEntityId.Value, ct);
    if (batch == null)
        return;

    using var doc = JsonDocument.Parse(completedJob.ResultJson);
    var root = doc.RootElement;
    var tasks = ExtractPlannerTasks(root);
    if (tasks.Count == 0)
    {
        batch.PlanJson = completedJob.ResultJson;
        batch.CanonicalRequestJson = root.TryGetProperty("canonicalRequest", out var invalidCanonical) ? invalidCanonical.GetRawText() : batch.CanonicalRequestJson;
        batch.CoverageJson = root.TryGetProperty("coverage", out var invalidCoverage) ? invalidCoverage.GetRawText() : batch.CoverageJson;
        batch.SummaryJson = root.TryGetProperty("summary", out var invalidSummary)
            ? JsonSerializer.Serialize(new { summary = invalidSummary.ValueKind == JsonValueKind.String ? invalidSummary.GetString() : invalidSummary.GetRawText() }, JsonOptions)
            : batch.SummaryJson;
        batch.Status = isReplan ? "batch-replan-invalid-result" : "batch-plan-invalid-result";
        batch.CurrentStage = isReplan ? AiFoundryStages.BatchReplan : AiFoundryStages.BatchPlan;
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await CreateBatchDecisionLogAsync(batch.Id, null, completedJob.Id, isReplan ? AiFoundryStages.BatchReplan : AiFoundryStages.BatchPlan, isReplan ? "batch-replan-invalid" : "batch-plan-invalid", "Planner result не содержит plan.tasks/items/slots. Batch оставлен на planning stage.", completedJob.ResultJson, ct);
        return;
    }

    var taskBlueprints = new List<object>();
    var taskBlueprintsByIndex = new Dictionary<int, dynamic>();
    var blueprintRows = new List<dynamic>();
    int fallbackIndex = 1;
    foreach (var taskNode in tasks)
    {
        var slotIndex = taskNode.TryGetProperty("index", out var iNode) && iNode.TryGetInt32(out var idx) ? idx : fallbackIndex;
        var titleHint = taskNode.TryGetProperty("titleHint", out var titleNode) && titleNode.ValueKind == JsonValueKind.String ? titleNode.GetString() : null;
        var targetSkill = taskNode.TryGetProperty("targetSkill", out var ts) && ts.ValueKind == JsonValueKind.String ? ts.GetString() : null;
        var difficultyTarget = taskNode.TryGetProperty("difficultyTarget", out var dt) && dt.TryGetInt32(out var d) ? d : 2;
        var microGoal = taskNode.TryGetProperty("microGoal", out var mg) && mg.ValueKind == JsonValueKind.String ? mg.GetString() : null;
        var whyItExists = taskNode.TryGetProperty("whyItExists", out var whyNode) && whyNode.ValueKind == JsonValueKind.String ? whyNode.GetString() : null;
        var antiDuplicateHints = taskNode.TryGetProperty("antiDuplicateHints", out var antiNode) && antiNode.ValueKind == JsonValueKind.Array
            ? antiNode.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(6).ToList()
            : new List<string>();

        var blueprint = new
        {
            Index = slotIndex,
            TitleHint = string.IsNullOrWhiteSpace(titleHint) ? targetSkill : titleHint,
            TargetSkill = targetSkill,
            DifficultyTarget = difficultyTarget,
            MicroGoal = microGoal,
            WhyItExists = whyItExists,
            AntiDuplicateHints = antiDuplicateHints,
            Raw = taskNode.GetRawText(),
        };
        blueprintRows.Add(blueprint);
        taskBlueprints.Add(new
        {
            blueprint.Index,
            blueprint.TitleHint,
            blueprint.TargetSkill,
            blueprint.DifficultyTarget,
            blueprint.MicroGoal,
            blueprint.WhyItExists,
            blueprint.AntiDuplicateHints,
        });
        taskBlueprintsByIndex[slotIndex] = blueprint;
        fallbackIndex++;
    }

    batch.PlanJson = JsonSerializer.Serialize(new { tasks = taskBlueprints }, JsonOptions);
    batch.CanonicalRequestJson = root.TryGetProperty("canonicalRequest", out var c) ? c.GetRawText() : batch.CanonicalRequestJson;
    batch.CourseProfileJson = root.TryGetProperty("courseProfile", out var cp) ? cp.GetRawText() : batch.CourseProfileJson;
    batch.GapAnalysisJson = root.TryGetProperty("gapAnalysis", out var ga) ? ga.GetRawText() : batch.GapAnalysisJson;
    batch.CoverageJson = root.TryGetProperty("coverage", out var cov) ? cov.GetRawText() : batch.CoverageJson;
    batch.SummaryJson = root.TryGetProperty("summary", out var s)
        ? JsonSerializer.Serialize(new { summary = s.ValueKind == JsonValueKind.String ? s.GetString() : s.GetRawText() }, JsonOptions)
        : batch.SummaryJson;
    batch.Status = isReplan ? "replanned" : "planned";
    batch.CurrentStage = AiFoundryStages.DraftGenerate;
    batch.UpdatedAtUtc = DateTime.UtcNow;

    var existingByIndex = batch.Items.ToDictionary(x => x.Index);
    var touchedIndexes = new HashSet<int>();
    var itemsForDrafts = new List<AiBatchItem>();
    var replanChanges = new List<object>();
    var createdCount = 0;
    var reusedCount = 0;
    var retiredCount = 0;

    foreach (dynamic blueprint in blueprintRows)
    {
        int slotIndex = blueprint.Index;
        touchedIndexes.Add(slotIndex);
        string? targetSkill = blueprint.TargetSkill;
        int difficultyTarget = blueprint.DifficultyTarget;
        string? microGoal = blueprint.MicroGoal;

        if (!existingByIndex.TryGetValue(slotIndex, out var item))
        {
            item = new AiBatchItem
            {
                Id = Guid.NewGuid(),
                BatchId = batch.Id,
                Index = slotIndex,
                TargetSkill = targetSkill,
                DifficultyTarget = difficultyTarget,
                MicroGoal = microGoal,
                Status = isReplan ? "replanned" : "planned",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                PlannerSignalsJson = isReplan ? JsonSerializer.Serialize(new { source = "batch-replan", appliedAtUtc = DateTime.UtcNow, slotIndex }, JsonOptions) : null,
            };
            _db.AiBatchItems.Add(item);
            itemsForDrafts.Add(item);
            createdCount++;
            replanChanges.Add(new { index = slotIndex, action = "created", targetSkill, difficultyTarget, microGoal });
        }
        else
        {
            var previousSnapshot = new
            {
                item.TargetSkill,
                item.DifficultyTarget,
                item.MicroGoal,
                item.Status,
                item.DraftId,
                updatedAtUtc = item.UpdatedAtUtc,
            };
            await ResetBatchItemForReplanAsync(item, isReplan, ct);
            item.TargetSkill = targetSkill;
            item.DifficultyTarget = difficultyTarget;
            item.MicroGoal = microGoal;
            item.Status = isReplan ? "replanned" : "planned";
            item.UpdatedAtUtc = DateTime.UtcNow;
            item.ReplanHistoryJson = AppendJsonHistory(item.ReplanHistoryJson, new { type = isReplan ? "batch-replan" : "batch-plan-refresh", appliedAtUtc = DateTime.UtcNow, previous = previousSnapshot, next = new { targetSkill, difficultyTarget, microGoal } });
            item.PlannerSignalsJson = MergeJsonSignals(item.PlannerSignalsJson, new { lastPlanAction = isReplan ? "replanned-slot" : "planned-slot", appliedAtUtc = DateTime.UtcNow, slotIndex });
            itemsForDrafts.Add(item);
            reusedCount++;
            replanChanges.Add(new { index = slotIndex, action = "reused", previous = previousSnapshot, next = new { targetSkill, difficultyTarget, microGoal } });
        }
        item.DecisionLogJson = MergeJsonSignals(item.DecisionLogJson, new { plannerSlot = JsonSerializer.Deserialize<object>(blueprint.Raw, JsonOptions) });
    }

    foreach (var item in batch.Items.Where(x => !touchedIndexes.Contains(x.Index)).ToList())
    {
        item.Status = "replanned-out";
        item.PlannerSignalsJson = MergeJsonSignals(item.PlannerSignalsJson, new { lastPlanAction = "retired", appliedAtUtc = DateTime.UtcNow, slotIndex = item.Index });
        item.ReplanHistoryJson = AppendJsonHistory(item.ReplanHistoryJson, new { type = "retired-from-plan", appliedAtUtc = DateTime.UtcNow, current = new { item.TargetSkill, item.DifficultyTarget, item.MicroGoal, item.Status } });
        item.UpdatedAtUtc = DateTime.UtcNow;
        retiredCount++;
        replanChanges.Add(new { index = item.Index, action = "retired", item.TargetSkill, item.DifficultyTarget, item.MicroGoal });
    }

    batch.ReplanLedgerJson = JsonSerializer.Serialize(new
    {
        version = isReplan ? "wave13-batch-replan" : "wave13-batch-plan",
        appliedAtUtc = DateTime.UtcNow,
        sourceJobId = completedJob.Id,
        createdCount,
        reusedCount,
        retiredCount,
        changes = replanChanges,
    }, JsonOptions);

    foreach (var item in itemsForDrafts)
    {
        item.HistoricalSlotPriorsJson = await BuildHistoricalSlotPriorsAsync(batch.CourseId, batch.AssignmentType, batch.Id, item.TargetSkill, item.DifficultyTarget, ct);
        item.UpdatedAtUtc = DateTime.UtcNow;
    }

    await _db.SaveChangesAsync(ct);
    await CreateBatchDecisionLogAsync(batch.Id, null, completedJob.Id, isReplan ? AiFoundryStages.BatchReplan : AiFoundryStages.BatchPlan, isReplan ? "batch-replanned" : "batch-planned", isReplan ? "Batch replan применён и слоты обновлены без сноса всей истории." : "Batch plan применён и слоты подготовлены к прямой draft generation.", batch.ReplanLedgerJson, ct);

    var peerBlueprints = itemsForDrafts
        .OrderBy(x => x.Index)
        .Select(x => new { x.Index, x.TargetSkill, x.DifficultyTarget, x.MicroGoal, x.Status })
        .ToList();

    var referenceAssignments = await BuildReferenceAssignmentsAsync(batch.CourseId, batch.AssignmentType, ct);
    var supportedLanguages = BuildSupportedLanguages(batch.AssignmentType, referenceAssignments, batch.Prompt);

    foreach (var item in itemsForDrafts.OrderBy(x => x.Index))
    {
        dynamic blueprint = taskBlueprintsByIndex[item.Index];
        await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = "assignment_generate_from_text",
            ParentJobId = completedJob.Id,
            TargetEntityType = "ai-batch-item",
            TargetEntityId = item.Id,
            CourseId = batch.CourseId,
            Priority = Math.Max(completedJob.Priority - 1, 1),
            StageCode = AiFoundryStages.DraftGenerate,
            StageLabel = isReplan ? "AI task regeneration" : "AI task generation",
            StageOrder = 30,
            InputJson = JsonSerializer.Serialize(new
            {
                requestType = "assignment_draft_generate",
                batchId = batch.Id,
                batchItemId = item.Id,
                courseId = batch.CourseId,
                assignmentType = batch.AssignmentType,
                prompt = batch.Prompt,
                mode = batch.Mode,
                difficulty = item.DifficultyTarget,
                titleHint = blueprint.TitleHint ?? item.TargetSkill,
                sourceText = blueprint.WhyItExists ?? item.MicroGoal ?? batch.Prompt,
                targetSkill = item.TargetSkill,
                microGoal = item.MicroGoal,
                notes = blueprint.WhyItExists,
                task = new
                {
                    item.Index,
                    item.TargetSkill,
                    item.DifficultyTarget,
                    item.MicroGoal,
                    blueprint.TitleHint,
                    blueprint.WhyItExists,
                    blueprint.AntiDuplicateHints,
                },
                plan = JsonSerializer.Deserialize<object>(batch.PlanJson ?? "{}"),
                courseProfile = JsonSerializer.Deserialize<object>(batch.CourseProfileJson ?? "{}"),
                gapAnalysis = JsonSerializer.Deserialize<object>(batch.GapAnalysisJson ?? "{}"),
                coverage = JsonSerializer.Deserialize<object>(batch.CoverageJson ?? "{}"),
                positiveMemory = JsonSerializer.Deserialize<object>(batch.PositiveMemoryJson ?? "{}"),
                batchMemory = JsonSerializer.Deserialize<object>(batch.BatchMemoryJson ?? "{}"),
                institutionalMemory = JsonSerializer.Deserialize<object>(batch.InstitutionalMemoryJson ?? "{}"),
                historicalPlannerPriors = JsonSerializer.Deserialize<object>(batch.HistoricalPlannerPriorsJson ?? "{}"),
                historicalSlotPriors = JsonSerializer.Deserialize<object>(item.HistoricalSlotPriorsJson ?? "{}"),
                plannerFeedback = JsonSerializer.Deserialize<object>(batch.PlannerFeedbackJson ?? "{}"),
                antiPatternMemory = JsonSerializer.Deserialize<object>(batch.AntiPatternMemoryJson ?? "{}"),
                decisionLogDigest = JsonSerializer.Deserialize<object>(batch.DecisionLogDigestJson ?? "{}"),
                batchPeerItems = peerBlueprints.Where(x => x.Index != item.Index).ToList(),
                referenceAssignments = referenceAssignments,
                targetSchema = BuildTargetSchema(batch.AssignmentType),
                qualityGates = BuildQualityGates(batch.AssignmentType),
                supportedLanguages = supportedLanguages,
                allowedLanguages = supportedLanguages,
                enableSelfCheck = true,
            }, JsonOptions),
        }, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
        item.Status = "draft-queued";
        item.UpdatedAtUtc = DateTime.UtcNow;
    }

    batch.Status = "drafting";
    batch.CurrentStage = AiFoundryStages.DraftGenerate;
    batch.UpdatedAtUtc = DateTime.UtcNow;
    await _db.SaveChangesAsync(ct);
}

    private async Task PersistTaskBriefAndEnqueueBriefReviewAsync(AiJob completedJob, CancellationToken ct)
    {
        ConsoleFoundry("brief-persist-start", completedJob);
        if (completedJob.TargetEntityId == null || string.IsNullOrWhiteSpace(completedJob.ResultJson))
            return;

        var item = await _db.AiBatchItems.Include(x => x.Batch).FirstOrDefaultAsync(x => x.Id == completedJob.TargetEntityId.Value, ct);
        if (item == null)
            return;

        item.BriefJson = completedJob.ResultJson;
        item.DecisionLogJson = ExtractJsonPropertyOrOriginal(completedJob.ResultJson, "decisionLog", item.DecisionLogJson);
        item.Status = "briefed";
        item.UpdatedAtUtc = DateTime.UtcNow;
        item.Batch.Status = "brief-review";
        item.Batch.CurrentStage = AiFoundryStages.BriefReview;
        item.Batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await CreateBatchDecisionLogAsync(item.BatchId, item.Id, completedJob.Id, AiFoundryStages.BriefGenerate, "brief-generated", "Task brief сохранён и отправлен на review.", completedJob.ResultJson, ct);
        await PersistReferenceSnapshotsAsync(item.BatchId, item.Id, completedJob.Id, "brief-input", completedJob.InputJson, ct);

        await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = AiFoundryJobTypes.BriefReview,
            ParentJobId = completedJob.Id,
            TargetEntityType = "ai-batch-item",
            TargetEntityId = item.Id,
            CourseId = item.Batch.CourseId,
            Priority = Math.Max(completedJob.Priority - 1, 1),
            StageCode = AiFoundryStages.BriefReview,
            StageLabel = "AI brief review",
            StageOrder = 25,
            InputJson = JsonSerializer.Serialize(new
            {
                requestType = "assignment_brief_review",
                batchId = item.BatchId,
                batchItemId = item.Id,
                courseId = item.Batch.CourseId,
                assignmentType = item.Batch.AssignmentType,
                prompt = item.Batch.Prompt,
                brief = JsonSerializer.Deserialize<object>(item.BriefJson ?? "{}"),
                plan = JsonSerializer.Deserialize<object>(item.Batch.PlanJson ?? "{}"),
                courseProfile = JsonSerializer.Deserialize<object>(item.Batch.CourseProfileJson ?? "{}"),
                gapAnalysis = JsonSerializer.Deserialize<object>(item.Batch.GapAnalysisJson ?? "{}"),
                positiveMemory = JsonSerializer.Deserialize<object>(item.Batch.PositiveMemoryJson ?? "{}"),
                batchMemory = JsonSerializer.Deserialize<object>(item.Batch.BatchMemoryJson ?? "{}"),
                historicalPlannerPriors = JsonSerializer.Deserialize<object>(item.Batch.HistoricalPlannerPriorsJson ?? "{}"),
                historicalSlotPriors = JsonSerializer.Deserialize<object>(item.HistoricalSlotPriorsJson ?? "{}"),
                plannerFeedback = JsonSerializer.Deserialize<object>(item.Batch.PlannerFeedbackJson ?? "{}"),
                antiPatternMemory = JsonSerializer.Deserialize<object>(item.Batch.AntiPatternMemoryJson ?? "{}"),
                referenceAssignments = await BuildReferenceAssignmentsAsync(item.Batch.CourseId, item.Batch.AssignmentType, ct),
            }, JsonOptions),
        }, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
    }
    private async Task PersistBriefReviewAndContinueAsync(AiJob completedJob, CancellationToken ct)
    {
        ConsoleFoundry("brief-review-persist-start", completedJob);
        if (completedJob.TargetEntityId == null || string.IsNullOrWhiteSpace(completedJob.ResultJson))
            return;

        var item = await _db.AiBatchItems.Include(x => x.Batch).FirstOrDefaultAsync(x => x.Id == completedJob.TargetEntityId.Value, ct);
        if (item == null)
            return;

        item.BriefReviewJson = completedJob.ResultJson;
        item.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await CreateBatchDecisionLogAsync(item.BatchId, item.Id, completedJob.Id, AiFoundryStages.BriefReview, "brief-reviewed", "Brief review завершён.", completedJob.ResultJson, ct);

        string status = "passed";
        try
        {
            using var doc = JsonDocument.Parse(completedJob.ResultJson);
            if (doc.RootElement.TryGetProperty("status", out var statusNode) && statusNode.ValueKind == JsonValueKind.String)
                status = statusNode.GetString() ?? "passed";
        }
        catch { }

        var existingBriefRepairs = await _db.AiJobs.CountAsync(x => x.Type == AiFoundryJobTypes.BriefRepair && x.TargetEntityId == item.Id, ct);
        if ((status == "failed" || status == "needs-review") && existingBriefRepairs < 2)
        {
            await EnqueueAsync(new CreateAiJobRequestDto
            {
                Type = AiFoundryJobTypes.BriefRepair,
                ParentJobId = completedJob.Id,
                TargetEntityType = "ai-batch-item",
                TargetEntityId = item.Id,
                CourseId = item.Batch.CourseId,
                Priority = Math.Max(completedJob.Priority - 1, 1),
                StageCode = AiFoundryStages.BriefRepair,
                StageLabel = "AI brief repair",
                StageOrder = 27,
                InputJson = JsonSerializer.Serialize(new
                {
                    requestType = "assignment_brief_repair",
                    batchId = item.BatchId,
                    batchItemId = item.Id,
                    courseId = item.Batch.CourseId,
                    assignmentType = item.Batch.AssignmentType,
                    prompt = item.Batch.Prompt,
                    brief = JsonSerializer.Deserialize<object>(item.BriefJson ?? "{}"),
                    briefReview = JsonSerializer.Deserialize<object>(item.BriefReviewJson ?? "{}"),
                    plan = JsonSerializer.Deserialize<object>(item.Batch.PlanJson ?? "{}"),
                    courseProfile = JsonSerializer.Deserialize<object>(item.Batch.CourseProfileJson ?? "{}"),
                    gapAnalysis = JsonSerializer.Deserialize<object>(item.Batch.GapAnalysisJson ?? "{}"),
                    positiveMemory = JsonSerializer.Deserialize<object>(item.Batch.PositiveMemoryJson ?? "{}"),
                    batchMemory = JsonSerializer.Deserialize<object>(item.Batch.BatchMemoryJson ?? "{}"),
                    historicalPlannerPriors = JsonSerializer.Deserialize<object>(item.Batch.HistoricalPlannerPriorsJson ?? "{}"),
                    historicalSlotPriors = JsonSerializer.Deserialize<object>(item.HistoricalSlotPriorsJson ?? "{}"),
                    plannerFeedback = JsonSerializer.Deserialize<object>(item.Batch.PlannerFeedbackJson ?? "{}"),
                    antiPatternMemory = JsonSerializer.Deserialize<object>(item.Batch.AntiPatternMemoryJson ?? "{}"),
                    referenceAssignments = await BuildReferenceAssignmentsAsync(item.Batch.CourseId, item.Batch.AssignmentType, ct),
                    repairCount = existingBriefRepairs,
                }, JsonOptions),
            }, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
            item.Status = "brief-needs-fix";
            item.Batch.Status = "brief-repair";
            item.Batch.CurrentStage = AiFoundryStages.BriefRepair;
            item.Batch.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return;
        }

        using var briefDoc = JsonDocument.Parse(item.BriefJson ?? "{}");
        var root = briefDoc.RootElement;
        var prompt = root.TryGetProperty("generationPrompt", out var gp) && gp.ValueKind == JsonValueKind.String
            ? gp.GetString()
            : root.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString() : item.MicroGoal;
        var sourceText = root.TryGetProperty("sourceText", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var difficulty = root.TryGetProperty("difficultyTarget", out var dn) && dn.TryGetInt32(out var di) ? di : item.DifficultyTarget;
        var titleHint = root.TryGetProperty("titleHint", out var th) && th.ValueKind == JsonValueKind.String ? th.GetString() : item.TargetSkill;

        item.Status = "brief-approved";
        item.HistoricalSlotPriorsJson = await BuildHistoricalSlotPriorsAsync(item.Batch.CourseId, item.Batch.AssignmentType, item.BatchId, item.TargetSkill, item.DifficultyTarget, ct);
        item.Batch.Status = "reference-pack";
        item.Batch.CurrentStage = AiFoundryStages.ReferencePackBuild;
        item.Batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = AiFoundryJobTypes.ReferencePackBuild,
            ParentJobId = completedJob.Id,
            TargetEntityType = "ai-batch-item",
            TargetEntityId = item.Id,
            CourseId = item.Batch.CourseId,
            Priority = Math.Max(completedJob.Priority - 1, 1),
            StageCode = AiFoundryStages.ReferencePackBuild,
            StageLabel = "AI reference pack build",
            StageOrder = 29,
            InputJson = JsonSerializer.Serialize(new
            {
                requestType = "assignment_reference_pack_build",
                batchId = item.BatchId,
                batchItemId = item.Id,
                courseId = item.Batch.CourseId,
                assignmentType = item.Batch.AssignmentType,
                prompt = prompt ?? item.Batch.Prompt,
                titleHint,
                difficulty,
                sourceText,
                notes,
                brief = JsonSerializer.Deserialize<object>(item.BriefJson ?? "{}"),
                briefReview = JsonSerializer.Deserialize<object>(item.BriefReviewJson ?? "{}"),
                plan = JsonSerializer.Deserialize<object>(item.Batch.PlanJson ?? "{}"),
                courseProfile = JsonSerializer.Deserialize<object>(item.Batch.CourseProfileJson ?? "{}"),
                gapAnalysis = JsonSerializer.Deserialize<object>(item.Batch.GapAnalysisJson ?? "{}"),
                assignmentOntology = JsonSerializer.Deserialize<object>(item.Batch.AssignmentOntologyJson ?? "{}"),
                exemplarSignals = JsonSerializer.Deserialize<object>(item.Batch.ExemplarSignalsJson ?? "{}"),
                negativeMemory = JsonSerializer.Deserialize<object>(item.Batch.NegativeMemoryJson ?? "{}"),
                positiveMemory = JsonSerializer.Deserialize<object>(item.Batch.PositiveMemoryJson ?? "{}"),
                batchMemory = JsonSerializer.Deserialize<object>(item.Batch.BatchMemoryJson ?? "{}"),
                historicalPlannerPriors = JsonSerializer.Deserialize<object>(item.Batch.HistoricalPlannerPriorsJson ?? "{}"),
                historicalSlotPriors = JsonSerializer.Deserialize<object>(item.HistoricalSlotPriorsJson ?? "{}"),
                plannerFeedback = JsonSerializer.Deserialize<object>(item.Batch.PlannerFeedbackJson ?? "{}"),
                antiPatternMemory = JsonSerializer.Deserialize<object>(item.Batch.AntiPatternMemoryJson ?? "{}"),
                referenceAssignments = await BuildReferenceAssignmentsAsync(item.Batch.CourseId, item.Batch.AssignmentType, ct),
                targetSchema = BuildTargetSchema(item.Batch.AssignmentType),
                qualityGates = BuildQualityGates(item.Batch.AssignmentType),
            }, JsonOptions),
        }, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
    }

    private async Task PersistBriefRepairAndRequeueReviewAsync(AiJob completedJob, CancellationToken ct)
    {
        ConsoleFoundry("brief-repair-persist-start", completedJob);
        if (completedJob.TargetEntityId == null || string.IsNullOrWhiteSpace(completedJob.ResultJson))
            return;

        var item = await _db.AiBatchItems.Include(x => x.Batch).FirstOrDefaultAsync(x => x.Id == completedJob.TargetEntityId.Value, ct);
        if (item == null)
            return;

        item.BriefJson = completedJob.ResultJson;
        item.Status = "brief-repaired";
        item.UpdatedAtUtc = DateTime.UtcNow;
        item.Batch.Status = "brief-review";
        item.Batch.CurrentStage = AiFoundryStages.BriefReview;
        item.Batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await CreateBatchDecisionLogAsync(item.BatchId, item.Id, completedJob.Id, AiFoundryStages.BriefRepair, "brief-repaired", "Brief repaired и отправлен на повторный review.", completedJob.ResultJson, ct);

        await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = AiFoundryJobTypes.BriefReview,
            ParentJobId = completedJob.Id,
            TargetEntityType = "ai-batch-item",
            TargetEntityId = item.Id,
            CourseId = item.Batch.CourseId,
            Priority = Math.Max(completedJob.Priority - 1, 1),
            StageCode = AiFoundryStages.BriefReview,
            StageLabel = "AI brief review",
            StageOrder = 28,
            InputJson = JsonSerializer.Serialize(new
            {
                requestType = "assignment_brief_review",
                batchId = item.BatchId,
                batchItemId = item.Id,
                courseId = item.Batch.CourseId,
                assignmentType = item.Batch.AssignmentType,
                prompt = item.Batch.Prompt,
                brief = JsonSerializer.Deserialize<object>(item.BriefJson ?? "{}"),
                plan = JsonSerializer.Deserialize<object>(item.Batch.PlanJson ?? "{}"),
                courseProfile = JsonSerializer.Deserialize<object>(item.Batch.CourseProfileJson ?? "{}"),
                gapAnalysis = JsonSerializer.Deserialize<object>(item.Batch.GapAnalysisJson ?? "{}"),
                positiveMemory = JsonSerializer.Deserialize<object>(item.Batch.PositiveMemoryJson ?? "{}"),
                batchMemory = JsonSerializer.Deserialize<object>(item.Batch.BatchMemoryJson ?? "{}"),
                historicalPlannerPriors = JsonSerializer.Deserialize<object>(item.Batch.HistoricalPlannerPriorsJson ?? "{}"),
                plannerFeedback = JsonSerializer.Deserialize<object>(item.Batch.PlannerFeedbackJson ?? "{}"),
                antiPatternMemory = JsonSerializer.Deserialize<object>(item.Batch.AntiPatternMemoryJson ?? "{}"),
                referenceAssignments = await BuildReferenceAssignmentsAsync(item.Batch.CourseId, item.Batch.AssignmentType, ct),
            }, JsonOptions),
        }, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
    }


    private async Task PersistReferencePackAndEnqueueDraftGenerationAsync(AiJob completedJob, CancellationToken ct)
    {
        ConsoleFoundry("reference-pack-persist-start", completedJob);
        if (completedJob.TargetEntityId == null || string.IsNullOrWhiteSpace(completedJob.ResultJson))
            return;

        var item = await _db.AiBatchItems.Include(x => x.Batch).FirstOrDefaultAsync(x => x.Id == completedJob.TargetEntityId.Value, ct);
        if (item == null)
            return;

        item.ReferencePackJson = completedJob.ResultJson;
        item.StylePackJson = ExtractJsonPropertyOrOriginal(completedJob.ResultJson, "stylePack", item.StylePackJson);
        item.PolicyPackJson = ExtractJsonPropertyOrOriginal(completedJob.ResultJson, "policyPack", item.PolicyPackJson);
        item.NegativePackJson = ExtractJsonPropertyOrOriginal(completedJob.ResultJson, "negativePack", item.NegativePackJson);
        item.ExemplarPackJson = ExtractJsonPropertyOrOriginal(completedJob.ResultJson, "exemplarPack", item.ExemplarPackJson);
        item.ReferenceSignalsJson = ExtractJsonPropertyOrOriginal(completedJob.ResultJson, "signals", item.ReferenceSignalsJson);
        item.Status = "reference-packed";
        item.UpdatedAtUtc = DateTime.UtcNow;
        item.Batch.Status = "drafting";
        item.Batch.CurrentStage = AiFoundryStages.DraftGenerate;
        item.Batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await CreateBatchDecisionLogAsync(item.BatchId, item.Id, completedJob.Id, AiFoundryStages.ReferencePackBuild, "reference-pack-ready", "Reference pack собран и передан в draft generation.", completedJob.ResultJson, ct);
        await PersistReferenceSnapshotsAsync(item.BatchId, item.Id, completedJob.Id, "reference-pack-input", completedJob.InputJson, ct);

        object? packObj = null;
        try { packObj = JsonSerializer.Deserialize<object>(item.ReferencePackJson ?? "{}"); } catch { }
        object? briefObj = null;
        try { briefObj = JsonSerializer.Deserialize<object>(item.BriefJson ?? "{}"); } catch { }
        using var briefDoc = JsonDocument.Parse(item.BriefJson ?? "{}");
        var root = briefDoc.RootElement;
        var prompt = root.TryGetProperty("generationPrompt", out var gp) && gp.ValueKind == JsonValueKind.String ? gp.GetString() : item.Batch.Prompt;
        var sourceText = root.TryGetProperty("sourceText", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var difficulty = root.TryGetProperty("difficultyTarget", out var dn) && dn.TryGetInt32(out var di) ? di : item.DifficultyTarget;
        var titleHint = root.TryGetProperty("titleHint", out var th) && th.ValueKind == JsonValueKind.String ? th.GetString() : item.TargetSkill;

        var peerItems = await _db.AiBatchItems
            .Where(x => x.BatchId == item.BatchId && x.Id != item.Id)
            .OrderBy(x => x.Index)
            .Select(x => new
            {
                x.Id,
                x.Index,
                x.TargetSkill,
                x.DifficultyTarget,
                x.MicroGoal,
                x.Status,
                x.DraftId
            })
            .ToListAsync(ct);

        var generationInput = await BuildGenerationInputAsync(
            requestType: "assignment_draft_generate",
            assignmentType: item.Batch.AssignmentType,
            courseId: item.Batch.CourseId,
            prompt: prompt,
            titleHint: titleHint,
            difficulty: difficulty,
            count: 1,
            notes: notes,
            enableSelfCheck: true,
            sourceText: sourceText,
            file: null,
            ct: ct,
            referenceAssignmentsOverride: packObj,
            additional: new
            {
                brief = briefObj,
                referencePack = packObj,
                task = new
                {
                    item.Id,
                    item.Index,
                    item.TargetSkill,
                    item.DifficultyTarget,
                    item.MicroGoal,
                    item.Status,
                    item.RepairCount,
                },
                batchId = item.BatchId,
                batchItemId = item.Id,
                batchPeerItems = peerItems,
                positiveMemory = TryDeserializeJsonObject(item.Batch.PositiveMemoryJson),
                batchMemory = TryDeserializeJsonObject(item.Batch.BatchMemoryJson),
                historicalPlannerPriors = TryDeserializeJsonObject(item.Batch.HistoricalPlannerPriorsJson),
                historicalSlotPriors = TryDeserializeJsonObject(item.HistoricalSlotPriorsJson),
                plannerFeedback = TryDeserializeJsonObject(item.Batch.PlannerFeedbackJson),
                antiPatternMemory = TryDeserializeJsonObject(item.Batch.AntiPatternMemoryJson),
            });

        await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = "assignment_generate_from_text",
            ParentJobId = completedJob.Id,
            TargetEntityType = "ai-batch-item",
            TargetEntityId = item.Id,
            CourseId = item.Batch.CourseId,
            Priority = Math.Max(completedJob.Priority - 1, 1),
            StageCode = AiFoundryStages.DraftGenerate,
            StageLabel = "AI task draft generation",
            StageOrder = 30,
            InputJson = JsonSerializer.Serialize(generationInput, JsonOptions),
        }, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
    }

    private async Task PersistBatchReviewAsync(AiJob completedJob, CancellationToken ct)
    {
        if (completedJob.TargetEntityId == null || string.IsNullOrWhiteSpace(completedJob.ResultJson))
            return;
        var batch = await _db.AiBatches.FirstOrDefaultAsync(x => x.Id == completedJob.TargetEntityId.Value, ct);
        if (batch == null)
            return;
        batch.BatchReviewJson = completedJob.ResultJson;
        batch.ReviewLedgerJson = completedJob.ResultJson;
        batch.Status = "batch-reviewed";
        batch.CurrentStage = AiFoundryStages.StudentJourneyReview;
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await CreateBatchDecisionLogAsync(batch.Id, null, completedJob.Id, AiFoundryStages.BatchReview, "batch-review-ready", "Batch coherence review завершён.", completedJob.ResultJson, ct);
    }

    private async Task PersistStudentJourneyReviewAsync(AiJob completedJob, CancellationToken ct)
    {
        if (completedJob.TargetEntityId == null || string.IsNullOrWhiteSpace(completedJob.ResultJson))
            return;
        var batch = await _db.AiBatches.FirstOrDefaultAsync(x => x.Id == completedJob.TargetEntityId.Value, ct);
        if (batch == null)
            return;
        batch.StudentJourneyJson = completedJob.ResultJson;
        batch.Status = "journey-reviewed";
        batch.CurrentStage = AiFoundryStages.BatchPublishPrepare;
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await CreateBatchDecisionLogAsync(batch.Id, null, completedJob.Id, AiFoundryStages.StudentJourneyReview, "student-journey-reviewed", "Student journey simulation завершена.", completedJob.ResultJson, ct);
    }

    private async Task PersistBatchPublishPrepareAsync(AiJob completedJob, CancellationToken ct)
    {
        if (completedJob.TargetEntityId == null || string.IsNullOrWhiteSpace(completedJob.ResultJson))
            return;
        var batch = await _db.AiBatches.FirstOrDefaultAsync(x => x.Id == completedJob.TargetEntityId.Value, ct);
        if (batch == null)
            return;
        batch.PublicationAuditJson = completedJob.ResultJson;
        batch.PublishPackJson = await BuildPublishPackJsonAsync(batch.Id, completedJob.ResultJson, ct);
        batch.QualityLedgerJson = await BuildQualityLedgerJsonAsync(batch.Id, completedJob.ResultJson, ct);
        batch.ExportManifestJson = await BuildExportManifestJsonAsync(batch.Id, batch.PublishPackJson, batch.QualityLedgerJson, completedJob.ResultJson, ct);
        batch.Status = ExtractPublishPackReadiness(batch.PublishPackJson) ?? ExtractBatchPublishStatus(completedJob.ResultJson);
        batch.CurrentStage = AiFoundryStages.PlannerFeedback;
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await CreateBatchDecisionLogAsync(batch.Id, null, completedJob.Id, AiFoundryStages.BatchPublishPrepare, "batch-publication-audited", "Publication readiness audit завершён.", completedJob.ResultJson, ct);
    }

    private async Task MaybeEnqueueBatchReviewAsync(Guid batchId, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct)
    {
        Console.WriteLine($"[AiJobService][Foundry] maybe-enqueue-batch-review >>> batchId={batchId}");
        var batch = await _db.AiBatches.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == batchId, ct);
        if (batch == null)
            return;
        if (batch.Items.Count == 0)
            return;
        var pending = batch.Items.Any(x => x.DraftId == null || !(x.Status == "ready" || x.Status == "reviewed" || x.Status == "needs-review"));
        if (pending)
            return;
        var exists = await _db.AiJobs.AnyAsync(x => x.Type == AiFoundryJobTypes.BatchReview && x.TargetEntityId == batch.Id && (x.Status == "pending" || x.Status == "processing" || x.Status == "retry"), ct);
        if (exists)
            return;
        var peerDrafts = await _db.AiGeneratedAssignmentDrafts.AsNoTracking().Where(x => x.BatchId == batch.Id).OrderBy(x => x.CreatedAtUtc)
            .Select(x => new { id = x.Id, batchItemId = x.BatchItemId, title = x.Title, draftJson = x.DraftJson, assignmentType = x.AssignmentType, status = x.Status })
            .ToListAsync(ct);
        var batchItems = batch.Items
            .OrderBy(x => x.Index)
            .Select(x => new
            {
                x.Id,
                x.Index,
                x.TargetSkill,
                x.DifficultyTarget,
                x.MicroGoal,
                x.Status,
                x.DraftId,
                x.RepairCount,
                scorecard = TryDeserializeJsonObject(x.ScorecardJson),
                briefReview = TryDeserializeJsonObject(x.BriefReviewJson),
                contextReview = TryDeserializeJsonObject(x.ContextReviewJson),
            })
            .ToList();
        await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = AiFoundryJobTypes.BatchReview,
            TargetEntityType = "ai-batch",
            TargetEntityId = batch.Id,
            CourseId = batch.CourseId,
            Priority = 5,
            StageCode = AiFoundryStages.BatchReview,
            StageLabel = "AI batch review",
            StageOrder = 90,
            InputJson = JsonSerializer.Serialize(new
            {
                requestType = "assignment_batch_review",
                batchId = batch.Id,
                courseId = batch.CourseId,
                assignmentType = batch.AssignmentType,
                prompt = batch.Prompt,
                mode = batch.Mode,
                count = batch.RequestedCount,
                plan = JsonSerializer.Deserialize<object>(batch.PlanJson ?? "{}"),
                courseProfile = JsonSerializer.Deserialize<object>(batch.CourseProfileJson ?? "{}"),
                gapAnalysis = JsonSerializer.Deserialize<object>(batch.GapAnalysisJson ?? "{}"),
                coverage = JsonSerializer.Deserialize<object>(batch.CoverageJson ?? "{}"),
                batchItems,
                batchPeerDrafts = peerDrafts.Select(x => new
                {
                    x.id,
                    x.batchItemId,
                    x.title,
                    descriptionSummary = ExtractDescriptionSummaryFromDraftJson(x.draftJson),
                    x.assignmentType,
                    x.status,
                    scorecard = batchItems.FirstOrDefault(i => i.DraftId == x.id)?.scorecard,
                }).ToList(),
            }, JsonOptions),
        }, createdByUserId, createdByDisplayName, ct);
    }

    private async Task MaybeEnqueueStudentJourneyReviewAsync(Guid batchId, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct)
    {
        Console.WriteLine($"[AiJobService][Foundry] maybe-enqueue-student-journey >>> batchId={batchId}");
        var batch = await _db.AiBatches.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == batchId, ct);
        if (batch == null || string.IsNullOrWhiteSpace(batch.BatchReviewJson))
            return;
        if (batch.Items.Count == 0)
            return;
        var pending = batch.Items.Any(x => x.DraftId == null || !(x.Status == "ready" || x.Status == "reviewed" || x.Status == "needs-review" || x.Status == "repaired"));
        if (pending)
            return;
        var exists = await _db.AiJobs.AnyAsync(x => x.Type == AiFoundryJobTypes.StudentJourneyReview && x.TargetEntityId == batch.Id && (x.Status == "pending" || x.Status == "processing" || x.Status == "retry"), ct);
        if (exists)
            return;

        var peerDrafts = await _db.AiGeneratedAssignmentDrafts.AsNoTracking().Where(x => x.BatchId == batch.Id).OrderBy(x => x.CreatedAtUtc)
            .Select(x => new { id = x.Id, batchItemId = x.BatchItemId, title = x.Title, draftJson = x.DraftJson, assignmentType = x.AssignmentType, status = x.Status })
            .ToListAsync(ct);
        var batchItems = batch.Items
            .OrderBy(x => x.Index)
            .Select(x => new
            {
                x.Id,
                x.Index,
                x.TargetSkill,
                x.DifficultyTarget,
                x.MicroGoal,
                x.Status,
                x.DraftId,
                x.RepairCount,
                scorecard = TryDeserializeJsonObject(x.ScorecardJson),
                briefReview = TryDeserializeJsonObject(x.BriefReviewJson),
                contextReview = TryDeserializeJsonObject(x.ContextReviewJson),
            })
            .ToList();

        await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = AiFoundryJobTypes.StudentJourneyReview,
            TargetEntityType = "ai-batch",
            TargetEntityId = batch.Id,
            CourseId = batch.CourseId,
            Priority = 4,
            StageCode = AiFoundryStages.StudentJourneyReview,
            StageLabel = "AI student journey review",
            StageOrder = 95,
            InputJson = JsonSerializer.Serialize(new
            {
                requestType = "assignment_student_journey_review",
                batchId = batch.Id,
                courseId = batch.CourseId,
                assignmentType = batch.AssignmentType,
                prompt = batch.Prompt,
                mode = batch.Mode,
                count = batch.RequestedCount,
                batchReview = TryDeserializeJsonObject(batch.BatchReviewJson),
                batchSummary = TryDeserializeJsonObject(batch.SummaryJson),
                plan = TryDeserializeJsonObject(batch.PlanJson),
                batchItems,
                batchPeerDrafts = peerDrafts.Select(x => new
                {
                    x.id,
                    x.batchItemId,
                    x.title,
                    descriptionSummary = ExtractDescriptionSummaryFromDraftJson(x.draftJson),
                    x.assignmentType,
                    x.status,
                    scorecard = batchItems.FirstOrDefault(i => i.DraftId == x.id)?.scorecard,
                }).ToList(),
            }, JsonOptions),
        }, createdByUserId, createdByDisplayName, ct);
    }

    private async Task MaybeEnqueueBatchPublishPrepareAsync(Guid batchId, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct)
    {
        Console.WriteLine($"[AiJobService][Foundry] maybe-enqueue-publish-prepare >>> batchId={batchId}");
        var batch = await _db.AiBatches.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == batchId, ct);
        if (batch == null || string.IsNullOrWhiteSpace(batch.BatchReviewJson) || string.IsNullOrWhiteSpace(batch.StudentJourneyJson))
            return;
        if (batch.Items.Count == 0)
            return;
        var exists = await _db.AiJobs.AnyAsync(x => x.Type == AiFoundryJobTypes.BatchPublishPrepare && x.TargetEntityId == batch.Id && (x.Status == "pending" || x.Status == "processing" || x.Status == "retry"), ct);
        if (exists)
            return;
        var batchItems = batch.Items
            .OrderBy(x => x.Index)
            .Select(x => new
            {
                x.Id,
                x.Index,
                x.TargetSkill,
                x.DifficultyTarget,
                x.MicroGoal,
                x.Status,
                x.RepairCount,
                scorecard = TryDeserializeJsonObject(x.ScorecardJson),
                briefReview = TryDeserializeJsonObject(x.BriefReviewJson),
                contextReview = TryDeserializeJsonObject(x.ContextReviewJson),
            })
            .ToList();

        await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = AiFoundryJobTypes.BatchPublishPrepare,
            TargetEntityType = "ai-batch",
            TargetEntityId = batch.Id,
            CourseId = batch.CourseId,
            Priority = 3,
            StageCode = AiFoundryStages.BatchPublishPrepare,
            StageLabel = "AI batch publish prepare",
            StageOrder = 98,
            InputJson = JsonSerializer.Serialize(new
            {
                requestType = "assignment_batch_publish_prepare",
                batchId = batch.Id,
                courseId = batch.CourseId,
                assignmentType = batch.AssignmentType,
                prompt = batch.Prompt,
                mode = batch.Mode,
                count = batch.RequestedCount,
                batchSummary = TryDeserializeJsonObject(batch.SummaryJson),
                batchReview = TryDeserializeJsonObject(batch.BatchReviewJson),
                studentJourney = TryDeserializeJsonObject(batch.StudentJourneyJson),
                positiveMemory = TryDeserializeJsonObject(batch.PositiveMemoryJson),
                batchMemory = TryDeserializeJsonObject(batch.BatchMemoryJson),
                batchItems,
            }, JsonOptions),
        }, createdByUserId, createdByDisplayName, ct);
    }

    private async Task MaybeEnqueuePlannerFeedbackAsync(Guid batchId, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct)
    {
        Console.WriteLine($"[AiJobService][Foundry] maybe-enqueue-planner-feedback >>> batchId={batchId}");
        var batch = await _db.AiBatches.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == batchId, ct);
        if (batch == null || string.IsNullOrWhiteSpace(batch.PublicationAuditJson))
            return;
        var exists = await _db.AiJobs.AnyAsync(x => x.Type == AiFoundryJobTypes.PlannerFeedback && x.TargetEntityId == batch.Id && (x.Status == "pending" || x.Status == "processing" || x.Status == "retry"), ct);
        if (exists)
            return;

        var batchItems = batch.Items
            .OrderBy(x => x.Index)
            .Select(x => new
            {
                x.Id,
                x.Index,
                x.TargetSkill,
                x.DifficultyTarget,
                x.MicroGoal,
                x.Status,
                x.RepairCount,
                scorecard = TryDeserializeJsonObject(x.ScorecardJson),
                briefReview = TryDeserializeJsonObject(x.BriefReviewJson),
                contextReview = TryDeserializeJsonObject(x.ContextReviewJson),
            })
            .ToList();

        var decisionLogs = await _db.AiDecisionLogs.AsNoTracking()
            .Where(x => x.BatchId == batch.Id)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(40)
            .Select(x => new { x.BatchItemId, x.StageCode, x.DecisionType, x.Message, x.CreatedAtUtc, x.PayloadJson })
            .ToListAsync(ct);

        await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = AiFoundryJobTypes.PlannerFeedback,
            TargetEntityType = "ai-batch",
            TargetEntityId = batch.Id,
            CourseId = batch.CourseId,
            Priority = 2,
            StageCode = AiFoundryStages.PlannerFeedback,
            StageLabel = "AI batch planner feedback",
            StageOrder = 99,
            InputJson = JsonSerializer.Serialize(new
            {
                requestType = "assignment_batch_planner_feedback",
                batchId = batch.Id,
                courseId = batch.CourseId,
                assignmentType = batch.AssignmentType,
                prompt = batch.Prompt,
                mode = batch.Mode,
                plan = TryDeserializeJsonObject(batch.PlanJson),
                batchSummary = TryDeserializeJsonObject(batch.SummaryJson),
                batchReview = TryDeserializeJsonObject(batch.BatchReviewJson),
                studentJourney = TryDeserializeJsonObject(batch.StudentJourneyJson),
                publicationAudit = TryDeserializeJsonObject(batch.PublicationAuditJson),
                publishPack = TryDeserializeJsonObject(batch.PublishPackJson),
                qualityLedger = TryDeserializeJsonObject(batch.QualityLedgerJson),
                exportManifest = TryDeserializeJsonObject(batch.ExportManifestJson),
                positiveMemory = TryDeserializeJsonObject(batch.PositiveMemoryJson),
                batchMemory = TryDeserializeJsonObject(batch.BatchMemoryJson),
                negativeMemory = TryDeserializeJsonObject(batch.NegativeMemoryJson),
                historicalPlannerPriors = TryDeserializeJsonObject(batch.HistoricalPlannerPriorsJson),
                decisionSummary = TryDeserializeJsonObject(batch.DecisionSummaryJson),
                batchItems,
                decisionLogs = decisionLogs.Select(x => new
                {
                    x.BatchItemId,
                    x.StageCode,
                    x.DecisionType,
                    x.Message,
                    x.CreatedAtUtc,
                    payload = TryDeserializeJsonObject(x.PayloadJson),
                }).ToList(),
            }, JsonOptions),
        }, createdByUserId, createdByDisplayName, ct);
    }

    private async Task PersistPlannerFeedbackAsync(AiJob completedJob, CancellationToken ct)
    {
        ConsoleFoundry("planner-feedback-persist-start", completedJob);
        if (completedJob.TargetEntityId == null || string.IsNullOrWhiteSpace(completedJob.ResultJson))
            return;

        var batch = await _db.AiBatches.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == completedJob.TargetEntityId.Value, ct);
        if (batch == null)
            return;

        batch.PlannerFeedbackJson = completedJob.ResultJson;
        batch.DecisionSummaryJson = ExtractJsonPropertyOrOriginal(completedJob.ResultJson, "decisionSummary", batch.DecisionSummaryJson);
        batch.InstitutionalMemoryJson = BuildInstitutionalMemoryJson(batch, completedJob.ResultJson);
        batch.AntiPatternMemoryJson = BuildAntiPatternMemoryJson(batch, completedJob.ResultJson);
        batch.CurrentStage = AiFoundryStages.PlannerFeedback;
        batch.Status = ExtractPlannerFeedbackStatus(completedJob.ResultJson, batch.Status);
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await CreateBatchDecisionLogAsync(batch.Id, null, completedJob.Id, AiFoundryStages.PlannerFeedback, "planner-feedback-ready", "Planner feedback и batch feedback loop подготовлены.", completedJob.ResultJson, ct);
        await AutoRoutePlannerFeedbackAsync(batch, completedJob, ct);
    }


private async Task AutoRoutePlannerFeedbackAsync(AiBatch batch, AiJob completedJob, CancellationToken ct)
{
    ConsoleFoundryBatch("planner-feedback-autoroute-start", batch, $"sourceJobId='{completedJob.Id}'");
    if (string.IsNullOrWhiteSpace(completedJob.ResultJson))
        return;
    try
    {
        using var doc = JsonDocument.Parse(completedJob.ResultJson);
        if (!doc.RootElement.TryGetProperty("slotRecommendations", out var slots) || slots.ValueKind != JsonValueKind.Array)
        {
            if (PlannerFeedbackRequiresReplan(completedJob.ResultJson))
                await MaybeEnqueueBatchReplanAsync(batch.Id, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
            return;
        }
        var itemMap = batch.Items.ToDictionary(x => x.Index);
        var routed = 0;
        foreach (var slot in slots.EnumerateArray())
        {
            var index = slot.TryGetProperty("index", out var idxNode) && idxNode.TryGetInt32(out var idx) ? idx : 0;
            if (index <= 0 || !itemMap.TryGetValue(index, out var item))
                continue;
            var targetAction = slot.TryGetProperty("targetAction", out var actionNode) && actionNode.ValueKind == JsonValueKind.String ? (actionNode.GetString() ?? string.Empty) : string.Empty;
            var fingerprint = ComputePlannerRecommendationFingerprint(slot);
            if (HasPlannerSignalFingerprint(item.PlannerSignalsJson, fingerprint))
                continue;
            item.PlannerSignalsJson = MergeJsonSignals(item.PlannerSignalsJson, new { fingerprint, appliedAtUtc = DateTime.UtcNow, targetAction, source = "planner-feedback", index });
            item.AntiPatternFlagsJson = MergeAntiPatternFlags(item.AntiPatternFlagsJson, batch.AntiPatternMemoryJson, completedJob.ResultJson, index);

            if (targetAction.Contains("brief", StringComparison.OrdinalIgnoreCase))
            {
                var pendingBrief = await _db.AiJobs.AnyAsync(x => x.Type == AiFoundryJobTypes.BriefRepair && x.TargetEntityId == item.Id && (x.Status == "pending" || x.Status == "processing" || x.Status == "retry"), ct);
                if (!pendingBrief)
                {
                    var repairCount = await _db.AiJobs.CountAsync(x => x.Type == AiFoundryJobTypes.BriefRepair && x.TargetEntityId == item.Id, ct);
                    if (repairCount < 3)
                    {
                        await EnqueueAsync(new CreateAiJobRequestDto
                        {
                            Type = AiFoundryJobTypes.BriefRepair,
                            ParentJobId = completedJob.Id,
                            TargetEntityType = "ai-batch-item",
                            TargetEntityId = item.Id,
                            CourseId = batch.CourseId,
                            Priority = 2,
                            StageCode = AiFoundryStages.BriefRepair,
                            StageLabel = "AI planner-routed brief repair",
                            StageOrder = 27,
                            InputJson = JsonSerializer.Serialize(new
                            {
                                requestType = "assignment_brief_repair",
                                batchId = batch.Id,
                                batchItemId = item.Id,
                                courseId = batch.CourseId,
                                assignmentType = batch.AssignmentType,
                                prompt = batch.Prompt,
                                brief = TryDeserializeJsonObject(item.BriefJson),
                                briefReview = TryDeserializeJsonObject(item.BriefReviewJson),
                                plan = TryDeserializeJsonObject(batch.PlanJson),
                                courseProfile = TryDeserializeJsonObject(batch.CourseProfileJson),
                                gapAnalysis = TryDeserializeJsonObject(batch.GapAnalysisJson),
                                positiveMemory = TryDeserializeJsonObject(batch.PositiveMemoryJson),
                                batchMemory = TryDeserializeJsonObject(batch.BatchMemoryJson),
                                historicalPlannerPriors = TryDeserializeJsonObject(batch.HistoricalPlannerPriorsJson),
                                historicalSlotPriors = TryDeserializeJsonObject(item.HistoricalSlotPriorsJson),
                                plannerFeedback = TryDeserializeJsonObject(batch.PlannerFeedbackJson),
                                antiPatternMemory = TryDeserializeJsonObject(batch.AntiPatternMemoryJson),
                                plannerSlotRecommendation = JsonSerializer.Deserialize<object>(slot.GetRawText(), JsonOptions),
                                decisionLogDigest = TryDeserializeJsonObject(batch.DecisionLogDigestJson),
                                referenceAssignments = await BuildReferenceAssignmentsAsync(batch.CourseId, batch.AssignmentType, ct),
                                repairCount,
                            }, JsonOptions),
                        }, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
                        item.Status = "brief-needs-fix";
                        item.UpdatedAtUtc = DateTime.UtcNow;
                        routed++;
                        await CreateBatchDecisionLogAsync(batch.Id, item.Id, completedJob.Id, AiFoundryStages.PlannerFeedback, "planner-feedback-routed", $"Planner feedback направил item #{index} в brief repair.", slot.GetRawText(), ct);
                    }
                }
            }
            else if (targetAction.Contains("reference", StringComparison.OrdinalIgnoreCase))
            {
                var pendingReference = await _db.AiJobs.AnyAsync(x => x.Type == AiFoundryJobTypes.ReferencePackBuild && x.TargetEntityId == item.Id && (x.Status == "pending" || x.Status == "processing" || x.Status == "retry"), ct);
                if (!pendingReference && !string.IsNullOrWhiteSpace(item.BriefJson))
                {
                    await EnqueueAsync(new CreateAiJobRequestDto
                    {
                        Type = AiFoundryJobTypes.ReferencePackBuild,
                        ParentJobId = completedJob.Id,
                        TargetEntityType = "ai-batch-item",
                        TargetEntityId = item.Id,
                        CourseId = batch.CourseId,
                        Priority = 2,
                        StageCode = AiFoundryStages.ReferencePackBuild,
                        StageLabel = "AI planner-routed reference pack rebuild",
                        StageOrder = 28,
                        InputJson = JsonSerializer.Serialize(new
                        {
                            requestType = "assignment_reference_pack_build",
                            batchId = batch.Id,
                            batchItemId = item.Id,
                            courseId = batch.CourseId,
                            assignmentType = batch.AssignmentType,
                            prompt = batch.Prompt,
                            brief = TryDeserializeJsonObject(item.BriefJson),
                            plan = TryDeserializeJsonObject(batch.PlanJson),
                            positiveMemory = TryDeserializeJsonObject(batch.PositiveMemoryJson),
                            batchMemory = TryDeserializeJsonObject(batch.BatchMemoryJson),
                            historicalPlannerPriors = TryDeserializeJsonObject(batch.HistoricalPlannerPriorsJson),
                            historicalSlotPriors = TryDeserializeJsonObject(item.HistoricalSlotPriorsJson),
                            plannerFeedback = TryDeserializeJsonObject(batch.PlannerFeedbackJson),
                            antiPatternMemory = TryDeserializeJsonObject(batch.AntiPatternMemoryJson),
                            decisionLogDigest = TryDeserializeJsonObject(batch.DecisionLogDigestJson),
                            plannerSlotRecommendation = JsonSerializer.Deserialize<object>(slot.GetRawText(), JsonOptions),
                            referenceAssignments = await BuildReferenceAssignmentsAsync(batch.CourseId, batch.AssignmentType, ct),
                        }, JsonOptions),
                    }, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
                    item.Status = "reference-pack-refresh";
                    item.UpdatedAtUtc = DateTime.UtcNow;
                    routed++;
                    await CreateBatchDecisionLogAsync(batch.Id, item.Id, completedJob.Id, AiFoundryStages.PlannerFeedback, "planner-feedback-reference-routed", $"Planner feedback направил item #{index} на reference pack rebuild.", slot.GetRawText(), ct);
                }
            }
        }

        if (routed > 0)
        {
            batch.Status = "planner-rerouting";
            batch.CurrentStage = AiFoundryStages.BriefRepair;
            batch.FeedbackLoopStateJson = JsonSerializer.Serialize(new { stage = "planner-rerouting", routedItems = routed, updatedAtUtc = DateTime.UtcNow }, JsonOptions);
            batch.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        else if (PlannerFeedbackRequiresReplan(completedJob.ResultJson))
        {
            await _db.SaveChangesAsync(ct);
            await MaybeEnqueueBatchReplanAsync(batch.Id, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
        }
        else
        {
            batch.FeedbackLoopStateJson = JsonSerializer.Serialize(new { stage = "planner-feedback-complete", routedItems = 0, updatedAtUtc = DateTime.UtcNow }, JsonOptions);
            batch.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }
    catch { }
}

    private static string ExtractPlannerFeedbackStatus(string? resultJson, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return fallback ?? "planner-feedback-ready";
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.TryGetProperty("decisionSummary", out var summary) && summary.ValueKind == JsonValueKind.Object)
            {
                var batchAction = summary.TryGetProperty("batchAction", out var actionNode) && actionNode.ValueKind == JsonValueKind.String ? (actionNode.GetString() ?? string.Empty).ToLowerInvariant() : string.Empty;
                var autoBriefRepairs = summary.TryGetProperty("autoBriefRepairCandidates", out var ab) && ab.TryGetInt32(out var c) ? c : 0;
                if (autoBriefRepairs > 0) return "planner-rerouting";
                if (batchAction == "ready") return "ready-for-publish";
                if (batchAction.Contains("replan")) return "planner-replan-recommended";
                if (batchAction.Contains("repair")) return "planner-feedback-repair-needed";
            }
        }
        catch { }
        return fallback ?? "planner-feedback-ready";
    }


private async Task MaybeEnqueueBatchReplanAsync(Guid batchId, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct)
{
    Console.WriteLine($"[AiJobService][Foundry] maybe-enqueue-batch-replan >>> batchId={batchId}");
    var batch = await _db.AiBatches.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == batchId, ct);
    if (batch == null || string.IsNullOrWhiteSpace(batch.PlannerFeedbackJson))
        return;
    var exists = await _db.AiJobs.AnyAsync(x => x.Type == AiFoundryJobTypes.BatchReplan && x.TargetEntityId == batch.Id && (x.Status == "pending" || x.Status == "processing" || x.Status == "retry"), ct);
    if (exists)
        return;
    var priorReplans = await _db.AiJobs.CountAsync(x => x.Type == AiFoundryJobTypes.BatchReplan && x.TargetEntityId == batch.Id && x.Status == "done", ct);
    if (priorReplans >= 2)
        return;
    if (!PlannerFeedbackRequiresReplan(batch.PlannerFeedbackJson))
        return;

    batch.HistoricalPlannerPriorsJson = await BuildHistoricalPlannerPriorsAsync(batch.CourseId, batch.AssignmentType, batch.Id, ct);

    await EnqueueAsync(new CreateAiJobRequestDto
    {
        Type = AiFoundryJobTypes.BatchReplan,
        TargetEntityType = "ai-batch",
        TargetEntityId = batch.Id,
        CourseId = batch.CourseId,
        Priority = 2,
        StageCode = AiFoundryStages.BatchReplan,
        StageLabel = "AI batch replan",
        StageOrder = 100,
        InputJson = JsonSerializer.Serialize(new
        {
            requestType = "assignment_batch_replan",
            batchId = batch.Id,
            courseId = batch.CourseId,
            assignmentType = batch.AssignmentType,
            prompt = batch.Prompt,
            mode = batch.Mode,
            count = batch.RequestedCount,
            courseProfile = JsonSerializer.Deserialize<object>(batch.CourseProfileJson ?? "{}"),
            gapAnalysis = JsonSerializer.Deserialize<object>(batch.GapAnalysisJson ?? "{}"),
            coverage = JsonSerializer.Deserialize<object>(batch.CoverageJson ?? "{}"),
            plan = JsonSerializer.Deserialize<object>(batch.PlanJson ?? "{}"),
            plannerFeedback = JsonSerializer.Deserialize<object>(batch.PlannerFeedbackJson ?? "{}"),
            historicalPlannerPriors = JsonSerializer.Deserialize<object>(batch.HistoricalPlannerPriorsJson ?? "{}"),
            studentJourney = JsonSerializer.Deserialize<object>(batch.StudentJourneyJson ?? "{}"),
            publicationAudit = JsonSerializer.Deserialize<object>(batch.PublicationAuditJson ?? "{}"),
            qualityLedger = JsonSerializer.Deserialize<object>(batch.QualityLedgerJson ?? "{}"),
            exportManifest = JsonSerializer.Deserialize<object>(batch.ExportManifestJson ?? "{}"),
            institutionalMemory = JsonSerializer.Deserialize<object>(batch.InstitutionalMemoryJson ?? "{}"),
            antiPatternMemory = JsonSerializer.Deserialize<object>(batch.AntiPatternMemoryJson ?? "{}"),
            decisionLogDigest = JsonSerializer.Deserialize<object>(batch.DecisionLogDigestJson ?? "{}"),
            existingItems = batch.Items.OrderBy(x => x.Index).Select(x => new { x.Id, x.Index, x.TargetSkill, x.DifficultyTarget, x.MicroGoal, x.Status, x.RepairCount, scorecard = TryDeserializeJsonObject(x.ScorecardJson) }).ToList(),
            referenceAssignments = await BuildReferenceAssignmentsAsync(batch.CourseId, batch.AssignmentType, ct),
            targetSchema = BuildTargetSchema(batch.AssignmentType),
            qualityGates = BuildQualityGates(batch.AssignmentType),
        }, JsonOptions),
    }, createdByUserId, createdByDisplayName, ct);

    batch.Status = "planner-replanning";
    batch.CurrentStage = AiFoundryStages.BatchReplan;
    batch.FeedbackLoopStateJson = JsonSerializer.Serialize(new { stage = "batch-replan-enqueued", enqueuedAtUtc = DateTime.UtcNow, priorReplans }, JsonOptions);
    batch.UpdatedAtUtc = DateTime.UtcNow;
    await _db.SaveChangesAsync(ct);
}

private static bool PlannerFeedbackRequiresReplan(string? plannerFeedbackJson)
{
    if (string.IsNullOrWhiteSpace(plannerFeedbackJson))
        return false;
    try
    {
        using var doc = JsonDocument.Parse(plannerFeedbackJson);
        if (doc.RootElement.TryGetProperty("decisionSummary", out var summary) && summary.ValueKind == JsonValueKind.Object)
        {
            var replanSuggested = summary.TryGetProperty("replanSuggested", out var rs) && rs.ValueKind is JsonValueKind.True or JsonValueKind.False && rs.GetBoolean();
            var batchAction = summary.TryGetProperty("batchAction", out var ba) && ba.ValueKind == JsonValueKind.String ? (ba.GetString() ?? string.Empty) : string.Empty;
            if (replanSuggested || batchAction.Contains("replan", StringComparison.OrdinalIgnoreCase))
                return true;
        }
    }
    catch { }
    return false;
}

private async Task ResetBatchItemForReplanAsync(AiBatchItem item, bool isReplan, CancellationToken ct)
{
    if (item.DraftId != null)
    {
        var existingDraft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.Id == item.DraftId.Value, ct);
        if (existingDraft != null)
        {
            existingDraft.BatchItemId = null;
            existingDraft.Status = isReplan ? "superseded-by-replan" : "superseded-by-plan-refresh";
            existingDraft.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    item.BriefJson = null;
    item.BriefReviewJson = null;
    item.ContextReviewJson = null;
    item.ReferencePackJson = null;
    item.StylePackJson = null;
    item.PolicyPackJson = null;
    item.NegativePackJson = null;
    item.ExemplarPackJson = null;
    item.ReferenceSignalsJson = null;
    item.DecisionLogJson = null;
    item.ScorecardJson = null;
    item.DraftId = null;
    item.AntiPatternFlagsJson = null;
    item.UpdatedAtUtc = DateTime.UtcNow;
}

    private static string ExtractBatchPublishStatus(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return "batch-reviewed";
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.TryGetProperty("publicationDecision", out var decision) && decision.ValueKind == JsonValueKind.Object)
            {
                if (decision.TryGetProperty("readiness", out var readiness) && readiness.ValueKind == JsonValueKind.String)
                {
                    return (readiness.GetString() ?? string.Empty).ToLowerInvariant() switch
                    {
                        "ready" => "ready-for-publish",
                        "partial" => "publish-partial",
                        "repair-needed" => "publication-repair-needed",
                        "blocked" => "publication-blocked",
                        _ => "publication-reviewed",
                    };
                }
            }
        }
        catch { }
        return "publication-reviewed";
    }

    private static void TryPopulateBatchSignalsFromCourseProfile(AiBatch batch, string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("courseProfile", out var cp))
            {
                if (cp.TryGetProperty("assignmentOntology", out var onto)) batch.AssignmentOntologyJson = onto.GetRawText();
                if (cp.TryGetProperty("exemplarSignals", out var ex)) batch.ExemplarSignalsJson = ex.GetRawText();
                if (cp.TryGetProperty("negativePatterns", out var neg)) batch.NegativeMemoryJson = neg.GetRawText();
            }
        }
        catch { }
    }

    private static void TryPopulateBatchSignalsFromGapAnalysis(AiBatch batch, string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("coverage", out var coverage)) batch.CoverageJson = coverage.GetRawText();
            if (root.TryGetProperty("decisionSummary", out var ds)) batch.DecisionSummaryJson = ds.GetRawText();
        }
        catch { }
    }

    private static string? ExtractJsonPropertyOrOriginal(string? resultJson, string propertyName, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return fallback;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.TryGetProperty(propertyName, out var node))
                return node.GetRawText();
        }
        catch { }
        return fallback;
    }

    private async Task<string> BuildPublishPackJsonAsync(Guid batchId, string? publicationAuditJson, CancellationToken ct)
    {
        var batch = await _db.AiBatches.AsNoTracking()
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == batchId, ct);
        if (batch == null)
            return JsonSerializer.Serialize(new { version = "wave15-publish-pack", status = "missing-batch" }, JsonOptions);

        var itemIds = batch.Items.Where(x => x.DraftId != null).Select(x => x.DraftId!.Value).ToList();
        var drafts = await _db.AiGeneratedAssignmentDrafts.AsNoTracking()
            .Where(x => itemIds.Contains(x.Id))
            .Select(x => new { x.Id, x.BatchItemId, x.Title, x.Status, x.AssignmentType, x.CreatedAtUtc, x.UpdatedAtUtc, x.DraftJson })
            .ToListAsync(ct);

        var readiness = ExtractBatchPublicationReadiness(publicationAuditJson) ?? batch.Status;
        var readyItems = new List<object>();
        var blockedItems = new List<object>();
        foreach (var item in batch.Items.OrderBy(x => x.Index))
        {
            var score = ExtractScorecardOverallScore(item.ScorecardJson) ?? 0;
            var route = ExtractScorecardPrimaryRoute(item.ScorecardJson);
            var draft = item.DraftId != null ? drafts.FirstOrDefault(x => x.Id == item.DraftId.Value) : null;
            var row = new
            {
                item.Index,
                item.Id,
                item.DraftId,
                title = draft?.Title,
                item.TargetSkill,
                item.DifficultyTarget,
                overallScore = score,
                primaryRoute = route,
                item.Status,
            };
            var isFallbackDraft = draft != null && IsFallbackDraftJson(draft.DraftJson);
            var publishableStatus = item.Status == "ready" || item.Status == "reviewed" || item.Status == "repaired";
            if (score >= 80 && item.DraftId != null && publishableStatus && !isFallbackDraft)
                readyItems.Add(row);
            else
                blockedItems.Add(new
                {
                    item.Index,
                    item.Id,
                    item.DraftId,
                    title = draft?.Title,
                    item.TargetSkill,
                    item.DifficultyTarget,
                    overallScore = score,
                    primaryRoute = route,
                    item.Status,
                    blockReason = isFallbackDraft ? "fallback-draft" : "quality-or-status",
                });
        }

        return JsonSerializer.Serialize(new
        {
            version = "wave15-publish-pack",
            generatedAtUtc = DateTime.UtcNow,
            batchId,
            readiness = blockedItems.Count == 0 && readyItems.Count > 0 ? "ready-for-publish" : "needs-review",
            shouldPublish = blockedItems.Count == 0 && readyItems.Count > 0,
            readyDrafts = readyItems,
            blockedDrafts = blockedItems,
            publishableCount = readyItems.Count,
            blockedCount = blockedItems.Count,
            notes = new[]
            {
                "Publish pack собирает готовые drafts и блокеры для частичной или полной публикации.",
                "Использовать pack как backend-ready Draft Pack для последующего UI/публикации."
            },
        }, JsonOptions);
    }


    private static string? ExtractPublishPackReadiness(string? publishPackJson)
    {
        if (string.IsNullOrWhiteSpace(publishPackJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(publishPackJson);
            if (doc.RootElement.TryGetProperty("readiness", out var node) && node.ValueKind == JsonValueKind.String)
                return node.GetString();
        }
        catch { }
        return null;
    }

    private async Task<string> BuildQualityLedgerJsonAsync(Guid batchId, string? publicationAuditJson, CancellationToken ct)
    {
        var batch = await _db.AiBatches.AsNoTracking()
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == batchId, ct);
        if (batch == null)
            return JsonSerializer.Serialize(new { version = "wave16-quality-ledger", status = "missing-batch" }, JsonOptions);

        var rows = batch.Items
            .OrderBy(x => x.Index)
            .Select(item =>
            {
                var overallScore = ExtractScorecardOverallScore(item.ScorecardJson) ?? 0;
                var primaryRoute = ExtractScorecardPrimaryRoute(item.ScorecardJson) ?? "unknown";
                var readinessBand = overallScore >= 90 ? "ready" : overallScore >= 80 ? "light-repair" : overallScore >= 65 ? "deep-repair" : overallScore >= 50 ? "regenerate-from-brief" : "rewrite-brief";
                var briefFindings = ExtractFindingDescriptors(item.BriefReviewJson, 3);
                var contextFindings = ExtractFindingDescriptors(item.ContextReviewJson, 3);
                var antiPatternFlags = ExtractFindingDescriptors(item.AntiPatternFlagsJson, 3);
                return new
                {
                    item.Index,
                    item.Id,
                    item.TargetSkill,
                    item.DifficultyTarget,
                    item.Status,
                    item.DraftId,
                    overallScore,
                    primaryRoute,
                    readinessBand,
                    briefFindingCount = briefFindings.Count,
                    contextFindingCount = contextFindings.Count,
                    antiPatternCount = antiPatternFlags.Count,
                    briefFindings,
                    contextFindings,
                    antiPatternFlags,
                    hasHistoricalSlotPriors = !string.IsNullOrWhiteSpace(item.HistoricalSlotPriorsJson),
                };
            })
            .ToList();

        var readinessCounts = rows
            .GroupBy(x => x.readinessBand, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { band = x.Key, count = x.Count() })
            .OrderByDescending(x => x.count)
            .ToList();
        var routeCounts = rows
            .GroupBy(x => x.primaryRoute, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { route = x.Key, count = x.Count() })
            .OrderByDescending(x => x.count)
            .ToList();
        var weakItems = rows.Where(x => x.overallScore < 80 || x.antiPatternCount > 0 || x.contextFindingCount > 0).Take(8).ToList();
        var publicationReadiness = ExtractBatchPublicationReadiness(publicationAuditJson) ?? batch.Status;

        return JsonSerializer.Serialize(new
        {
            version = "wave16-quality-ledger",
            generatedAtUtc = DateTime.UtcNow,
            batchId,
            publicationReadiness,
            itemsCount = rows.Count,
            averageOverallScore = rows.Count == 0 ? 0 : (int)Math.Round(rows.Average(x => x.overallScore), MidpointRounding.AwayFromZero),
            readinessCounts,
            routeCounts,
            weakItems,
            items = rows,
            notes = new[]
            {
                "Quality ledger собирает финальную item-level сводку по scorecard, findings и anti-pattern flags.",
                "Использовать ledger как backend-ready quality surface для publish/export и последующего UI."
            },
        }, JsonOptions);
    }

    private async Task<string> BuildExportManifestJsonAsync(Guid batchId, string? publishPackJson, string? qualityLedgerJson, string? publicationAuditJson, CancellationToken ct)
    {
        var batch = await _db.AiBatches.AsNoTracking()
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == batchId, ct);
        if (batch == null)
            return JsonSerializer.Serialize(new { version = "wave16-export-manifest", status = "missing-batch" }, JsonOptions);

        var publishPack = TryDeserializeJsonObject(publishPackJson);
        var qualityLedger = TryDeserializeJsonObject(qualityLedgerJson);
        var readiness = ExtractBatchPublicationReadiness(publicationAuditJson) ?? batch.Status;
        var shouldPublish = false;
        var publishableCount = 0;
        var blockedCount = batch.Items.Count;
        try
        {
            using var doc = JsonDocument.Parse(publishPackJson ?? "{}");
            var root = doc.RootElement;
            shouldPublish = root.TryGetProperty("shouldPublish", out var sp) && sp.ValueKind is JsonValueKind.True or JsonValueKind.False && sp.GetBoolean();
            publishableCount = root.TryGetProperty("publishableCount", out var pc) && pc.TryGetInt32(out var p) ? p : 0;
            blockedCount = root.TryGetProperty("blockedCount", out var bc) && bc.TryGetInt32(out var b) ? b : blockedCount;
        }
        catch { }

        var operation = shouldPublish && blockedCount == 0
            ? "publish-full"
            : publishableCount > 0
                ? "publish-partial"
                : "hold";

        var exportItems = batch.Items
            .OrderBy(x => x.Index)
            .Select(x => new
            {
                exportOrder = x.Index,
                x.Id,
                x.DraftId,
                x.TargetSkill,
                x.DifficultyTarget,
                x.Status,
                overallScore = ExtractScorecardOverallScore(x.ScorecardJson),
                primaryRoute = ExtractScorecardPrimaryRoute(x.ScorecardJson),
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            version = "wave16-export-manifest",
            generatedAtUtc = DateTime.UtcNow,
            batchId,
            readiness,
            operation,
            shouldPublish,
            publishableCount,
            blockedCount,
            exportItems,
            publishPack,
            qualityLedger,
            notes = new[]
            {
                "Export manifest собирает backend-ready операцию публикации: full, partial или hold.",
                "Использовать manifest как финальный слой перед внешней публикацией или Batch UI Lab."
            },
        }, JsonOptions);
    }

    private async Task<string> BuildHistoricalSlotPriorsAsync(Guid? courseId, string assignmentType, Guid currentBatchId, string? targetSkill, int difficultyTarget, CancellationToken ct)
    {
        if (courseId == null || string.IsNullOrWhiteSpace(targetSkill))
        {
            return JsonSerializer.Serialize(new
            {
                version = "wave15-historical-slot-priors",
                generatedAtUtc = DateTime.UtcNow,
                targetSkill,
                difficultyTarget,
                sourceItemCount = 0,
                strongExamples = Array.Empty<object>(),
                weakExamples = Array.Empty<object>(),
                routeHints = Array.Empty<object>(),
                notes = new[] { "Исторические slot priors пока пусты для этого item." },
            }, JsonOptions);
        }

        var normalizedSkill = targetSkill.Trim();
        var historicalItems = await _db.AiBatches.AsNoTracking()
            .Where(x => x.CourseId == courseId && x.AssignmentType == assignmentType && x.Id != currentBatchId)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(16)
            .SelectMany(x => x.Items.Select(i => new
            {
                BatchId = x.Id,
                x.Mode,
                x.Status,
                i.Index,
                i.TargetSkill,
                i.DifficultyTarget,
                i.ScorecardJson,
                i.BriefReviewJson,
                i.AntiPatternFlagsJson,
                i.ReferenceSignalsJson,
            }))
            .Where(i => i.TargetSkill != null && EF.Functions.Like(i.TargetSkill!, $"%{normalizedSkill}%"))
            .Take(24)
            .ToListAsync(ct);

        var strongExamples = historicalItems
            .Select(x => new
            {
                x.BatchId,
                x.Index,
                x.TargetSkill,
                x.DifficultyTarget,
                overallScore = ExtractScorecardOverallScore(x.ScorecardJson) ?? 0,
                primaryRoute = ExtractScorecardPrimaryRoute(x.ScorecardJson),
            })
            .Where(x => x.overallScore >= 85)
            .OrderByDescending(x => x.overallScore)
            .ThenBy(x => Math.Abs(x.DifficultyTarget - difficultyTarget))
            .Take(5)
            .ToList();

        var weakExamples = historicalItems
            .Select(x => new
            {
                x.BatchId,
                x.Index,
                x.TargetSkill,
                x.DifficultyTarget,
                overallScore = ExtractScorecardOverallScore(x.ScorecardJson) ?? 100,
                antiPatterns = ExtractFindingDescriptors(x.AntiPatternFlagsJson, 2),
                briefFindings = ExtractFindingDescriptors(x.BriefReviewJson, 2),
                primaryRoute = ExtractScorecardPrimaryRoute(x.ScorecardJson),
            })
            .Where(x => x.overallScore < 75 || x.antiPatterns.Count > 0 || x.briefFindings.Count > 0)
            .OrderBy(x => x.overallScore)
            .ThenBy(x => Math.Abs(x.DifficultyTarget - difficultyTarget))
            .Take(5)
            .ToList();

        var routeHints = historicalItems
            .Select(x => ExtractScorecardPrimaryRoute(x.ScorecardJson))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(x => x!, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { route = x.Key, count = x.Count() })
            .OrderByDescending(x => x.count)
            .Take(5)
            .ToList();

        var recommendedDifficultyBand = historicalItems
            .Select(x => x.DifficultyTarget)
            .DefaultIfEmpty(difficultyTarget)
            .Average();

        return JsonSerializer.Serialize(new
        {
            version = "wave15-historical-slot-priors",
            generatedAtUtc = DateTime.UtcNow,
            targetSkill = normalizedSkill,
            difficultyTarget,
            sourceItemCount = historicalItems.Count,
            recommendedDifficultyBand = (int)Math.Round(recommendedDifficultyBand, MidpointRounding.AwayFromZero),
            strongExamples,
            weakExamples,
            routeHints,
            notes = new[]
            {
                "Использовать slot priors для brief/review/reference pack именно по этому targetSkill.",
                "Не повторять weak examples и anti-patterns при генерации нового item."
            },
        }, JsonOptions);
    }

    private async Task<string> BuildHistoricalPlannerPriorsAsync(Guid? courseId, string assignmentType, Guid currentBatchId, CancellationToken ct)
    {
        if (courseId == null)
        {
            return JsonSerializer.Serialize(new
            {
                version = "wave14-historical-planner-priors",
                generatedAtUtc = DateTime.UtcNow,
                sourceBatchCount = 0,
                publicationOutcomes = Array.Empty<object>(),
                strongSkills = Array.Empty<object>(),
                weakSkills = Array.Empty<object>(),
                recurringRepairRoutes = Array.Empty<object>(),
                riskyTransitions = Array.Empty<object>(),
                antiPatterns = Array.Empty<object>(),
                notes = new[] { "Исторические priors пока пусты для этого курса." },
            }, JsonOptions);
        }

        var previousBatches = await _db.AiBatches.AsNoTracking()
            .Where(x => x.CourseId == courseId && x.AssignmentType == assignmentType && x.Id != currentBatchId)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(12)
            .Select(x => new
            {
                x.Id,
                x.Status,
                x.UpdatedAtUtc,
                x.PublicationAuditJson,
                x.InstitutionalMemoryJson,
                x.AntiPatternMemoryJson,
                x.BatchMemoryJson,
                Items = x.Items.Select(i => new
                {
                    i.Index,
                    i.TargetSkill,
                    i.DifficultyTarget,
                    i.ScorecardJson,
                    i.AntiPatternFlagsJson,
                }).ToList(),
            })
            .ToListAsync(ct);

        var publicationOutcomes = previousBatches
            .Select(x => ExtractBatchPublicationReadiness(x.PublicationAuditJson) ?? x.Status)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(x => x!, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { readiness = x.Key, count = x.Count() })
            .OrderByDescending(x => x.count)
            .ToList();

        var strongSkills = previousBatches
            .SelectMany(x => x.Items)
            .Select(x => new { x.TargetSkill, score = ExtractScorecardOverallScore(x.ScorecardJson) ?? 0 })
            .Where(x => !string.IsNullOrWhiteSpace(x.TargetSkill) && x.score >= 85)
            .GroupBy(x => x.TargetSkill!, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { targetSkill = x.Key, count = x.Count(), averageScore = (int)Math.Round(x.Average(v => v.score), MidpointRounding.AwayFromZero) })
            .OrderByDescending(x => x.count)
            .ThenByDescending(x => x.averageScore)
            .Take(10)
            .ToList();

        var weakSkills = previousBatches
            .SelectMany(x => x.Items)
            .Select(x => new { x.TargetSkill, score = ExtractScorecardOverallScore(x.ScorecardJson) ?? 100, antiPatterns = ExtractFindingDescriptors(x.AntiPatternFlagsJson, 3) })
            .Where(x => !string.IsNullOrWhiteSpace(x.TargetSkill) && (x.score < 75 || x.antiPatterns.Count > 0))
            .GroupBy(x => x.TargetSkill!, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { targetSkill = x.Key, count = x.Count(), averageScore = (int)Math.Round(x.Average(v => v.score), MidpointRounding.AwayFromZero) })
            .OrderByDescending(x => x.count)
            .ThenBy(x => x.averageScore)
            .Take(10)
            .ToList();

        var recurringRepairRoutes = previousBatches
            .SelectMany(x => ExtractHistoricalRouteCounts(x.InstitutionalMemoryJson))
            .GroupBy(x => x.Route, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { route = x.Key, count = x.Sum(v => v.Count) })
            .OrderByDescending(x => x.count)
            .Take(8)
            .ToList();

        var riskyTransitions = previousBatches
            .SelectMany(x => ExtractRiskyTransitions(x.BatchMemoryJson))
            .Take(12)
            .ToList();

        var antiPatterns = previousBatches
            .SelectMany(x => ExtractFindingDescriptors(x.AntiPatternMemoryJson, 6))
            .Take(16)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            version = "wave14-historical-planner-priors",
            generatedAtUtc = DateTime.UtcNow,
            sourceBatchCount = previousBatches.Count,
            publicationOutcomes,
            strongSkills,
            weakSkills,
            recurringRepairRoutes,
            riskyTransitions,
            antiPatterns,
            notes = new[]
            {
                "Использовать historical planner priors при следующем planning/replan шаге.",
                "Не повторять слабые targetSkill patterns и risky transitions из недавних batch waves.",
            },
        }, JsonOptions);
    }

    private static IReadOnlyList<(string Route, int Count)> ExtractHistoricalRouteCounts(string? json)
    {
        var result = new List<(string Route, int Count)>();
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("recurringRepairRoutes", out var routes) && routes.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in routes.EnumerateArray())
                {
                    if (!node.TryGetProperty("route", out var routeNode) || routeNode.ValueKind != JsonValueKind.String)
                        continue;
                    var route = routeNode.GetString();
                    if (string.IsNullOrWhiteSpace(route))
                        continue;
                    var count = node.TryGetProperty("count", out var countNode) && countNode.TryGetInt32(out var parsed) ? parsed : 1;
                    result.Add((route, count));
                }
            }
        }
        catch { }
        return result;
    }

    private async Task CreateBatchDecisionLogAsync(Guid batchId, Guid? batchItemId, Guid? jobId, string stageCode, string decisionType, string message, string? payloadJson, CancellationToken ct)
    {
        Console.WriteLine($"[AiJobService][Foundry] decision-log >>> batchId={batchId} batchItemId='{batchItemId}' jobId='{jobId}' stageCode='{stageCode}' decisionType='{decisionType}' message='{PreviewForConsole(message, 160)}' payloadLen={payloadJson?.Length ?? 0}");
        _db.AiDecisionLogs.Add(new AiDecisionLog
        {
            Id = Guid.NewGuid(),
            BatchId = batchId,
            BatchItemId = batchItemId,
            JobId = jobId,
            StageCode = stageCode,
            DecisionType = decisionType,
            Message = message,
            PayloadJson = payloadJson,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task PersistReferenceSnapshotsAsync(Guid batchId, Guid? batchItemId, Guid? jobId, string role, string? inputJson, CancellationToken ct)
    {
        Console.WriteLine($"[AiJobService][Foundry] reference-snapshot >>> batchId={batchId} batchItemId='{batchItemId}' jobId='{jobId}' role='{role}' inputLen={inputJson?.Length ?? 0}");
        if (string.IsNullOrWhiteSpace(inputJson))
            return;
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            if (!doc.RootElement.TryGetProperty("referenceAssignments", out var refsNode) || refsNode.ValueKind != JsonValueKind.Array)
                return;
            var existing = await _db.AiReferenceSnapshots.CountAsync(x => x.BatchId == batchId && x.BatchItemId == batchItemId && x.JobId == jobId && x.Role == role, ct);
            if (existing > 0)
                return;
            foreach (var node in refsNode.EnumerateArray().Take(12))
            {
                Guid? sourceCourseId = null;
                if (node.TryGetProperty("courseId", out var courseNode) && courseNode.ValueKind == JsonValueKind.String && Guid.TryParse(courseNode.GetString(), out var gid))
                    sourceCourseId = gid;
                var extId = node.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String ? idNode.GetString() : null;
                _db.AiReferenceSnapshots.Add(new AiReferenceSnapshot
                {
                    Id = Guid.NewGuid(),
                    BatchId = batchId,
                    BatchItemId = batchItemId,
                    JobId = jobId,
                    SourceCourseId = sourceCourseId,
                    SourceAssignmentExternalId = extId,
                    Role = role,
                    CompactSummaryJson = node.GetRawText(),
                    CreatedAtUtc = DateTime.UtcNow,
                });
            }
            await _db.SaveChangesAsync(ct);
        }
        catch { }
    }


    private static object? TryDeserializeJsonObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return JsonSerializer.Deserialize<object>(value, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractDescriptionSummaryFromDraftJson(string? draftJson)
    {
        if (string.IsNullOrWhiteSpace(draftJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(draftJson);
            if (!doc.RootElement.TryGetProperty("description", out var node) || node.ValueKind != JsonValueKind.String)
                return null;
            var value = node.GetString();
            if (string.IsNullOrWhiteSpace(value)) return null;
            var normalized = value.Replace("\r", " ").Replace("\n", " ");
            normalized = string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return normalized.Length <= 420 ? normalized : normalized[..420] + "...";
        }
        catch
        {
            return null;
        }
    }


    private static string AppendJsonHistory(string? existingJson, object entry)
    {
        JsonArray array;
        try
        {
            array = string.IsNullOrWhiteSpace(existingJson)
                ? new JsonArray()
                : (JsonNode.Parse(existingJson!) as JsonArray ?? new JsonArray(JsonNode.Parse(existingJson!)));
        }
        catch
        {
            array = new JsonArray();
        }

        array.Add(JsonSerializer.SerializeToNode(entry, JsonOptions));
        return array.ToJsonString(JsonOptions);
    }

    private static string MergeJsonSignals(string? existingJson, object patch)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(existingJson)
                ? new JsonObject()
                : (JsonNode.Parse(existingJson!) as JsonObject ?? new JsonObject());
        }
        catch
        {
            root = new JsonObject();
        }

        var patchNode = JsonSerializer.SerializeToNode(patch, JsonOptions) as JsonObject;
        if (patchNode != null)
        {
            foreach (var kv in patchNode)
                root[kv.Key] = kv.Value?.DeepClone();
        }

        if (patchNode != null && patchNode.TryGetPropertyValue("fingerprint", out var fpNode) && fpNode is JsonValue fpVal && fpVal.TryGetValue<string>(out var fp) && !string.IsNullOrWhiteSpace(fp))
        {
            var fps = root["fingerprints"] as JsonArray ?? new JsonArray();
            if (!fps.Any(x => string.Equals(x?.GetValue<string>(), fp, StringComparison.Ordinal)))
                fps.Add(fp);
            root["fingerprints"] = fps;
        }

        return root.ToJsonString(JsonOptions);
    }

    private static string ComputePlannerRecommendationFingerprint(JsonElement slot)
    {
        var raw = slot.GetRawText();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }

    private static bool HasPlannerSignalFingerprint(string? plannerSignalsJson, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(plannerSignalsJson) || string.IsNullOrWhiteSpace(fingerprint))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(plannerSignalsJson);
            if (doc.RootElement.TryGetProperty("fingerprints", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in arr.EnumerateArray())
                {
                    if (node.ValueKind == JsonValueKind.String && string.Equals(node.GetString(), fingerprint, StringComparison.Ordinal))
                        return true;
                }
            }
            if (doc.RootElement.TryGetProperty("fingerprint", out var single) && single.ValueKind == JsonValueKind.String)
                return string.Equals(single.GetString(), fingerprint, StringComparison.Ordinal);
        }
        catch { }
        return false;
    }

    private static string? MergeAntiPatternFlags(string? existingFlagsJson, string? antiPatternMemoryJson, string? plannerFeedbackJson, int index)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void addJsonArrayStrings(string? json, params string[] props)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var prop in props)
                {
                    if (doc.RootElement.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var n in arr.EnumerateArray())
                            if (n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())) values.Add(n.GetString()!);
                    }
                }
            }
            catch { }
        }
        addJsonArrayStrings(existingFlagsJson, "flags");
        addJsonArrayStrings(antiPatternMemoryJson, "antiPatterns", "flags");
        addJsonArrayStrings(plannerFeedbackJson, "antiPatterns", "flags");
        if (values.Count == 0) return existingFlagsJson;
        return JsonSerializer.Serialize(new { flags = values.OrderBy(x => x).ToArray(), slotIndex = index, updatedAtUtc = DateTime.UtcNow }, JsonOptions);
    }

    public async Task<bool> DeleteBatchAsync(Guid id, CancellationToken ct = default)
    {
        var batch = await _db.AiBatches
            .Include(b => b.Items)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (batch == null) return false;

        var batchItemIds = batch.Items.Select(i => i.Id).ToList();
        var draftIds = batch.Items.Where(i => i.DraftId.HasValue).Select(i => i.DraftId!.Value).ToList();

        // Delete drafts that belong to this batch
        if (draftIds.Count > 0)
            await _db.AiGeneratedAssignmentDrafts.Where(d => draftIds.Contains(d.Id)).ExecuteDeleteAsync(ct);

        // Delete decision logs & reference snapshots for this batch
        await _db.AiDecisionLogs.Where(dl => dl.BatchId == id).ExecuteDeleteAsync(ct);
        await _db.AiReferenceSnapshots.Where(rs => rs.BatchId == id).ExecuteDeleteAsync(ct);

        // Delete decision logs & reference snapshots for batch items
        if (batchItemIds.Count > 0)
        {
            await _db.AiDecisionLogs.Where(dl => dl.BatchItemId.HasValue && batchItemIds.Contains(dl.BatchItemId.Value)).ExecuteDeleteAsync(ct);
            await _db.AiReferenceSnapshots.Where(rs => rs.BatchItemId.HasValue && batchItemIds.Contains(rs.BatchItemId.Value)).ExecuteDeleteAsync(ct);
        }

        // Delete items, then batch
        if (batchItemIds.Count > 0)
            await _db.AiBatchItems.Where(i => batchItemIds.Contains(i.Id)).ExecuteDeleteAsync(ct);

        _db.AiBatches.Remove(batch);
        await _db.SaveChangesAsync(ct);
        return true;
    }

}
