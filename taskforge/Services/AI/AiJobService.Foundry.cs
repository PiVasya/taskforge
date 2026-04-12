using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using taskforge.Constants;
using taskforge.Data.Models.DTO.AI;
using taskforge.Data.Models.Entities.AI;

namespace taskforge.Services.AI;

public sealed partial class AiJobService
{
    private async Task SyncFoundryProgressAfterCompletionAsync(AiJob completedJob, CancellationToken ct)
    {
        ConsoleFoundry("sync-start", completedJob);
        if (string.Equals(completedJob.Type, AiFoundryJobTypes.CourseProfileBuild, StringComparison.OrdinalIgnoreCase))
        {
            await PersistCourseProfileAndEnqueueGapAnalysisAsync(completedJob, ct);
        }

        if (string.Equals(completedJob.Type, AiFoundryJobTypes.GapAnalysis, StringComparison.OrdinalIgnoreCase))
        {
            await PersistGapAnalysisAndEnqueueBatchPlanAsync(completedJob, ct);
        }

        if (string.Equals(completedJob.Type, AiFoundryJobTypes.BatchPlan, StringComparison.OrdinalIgnoreCase)
            || string.Equals(completedJob.Type, AiFoundryJobTypes.BatchReplan, StringComparison.OrdinalIgnoreCase))
        {
            await PersistBatchPlanAsync(completedJob, string.Equals(completedJob.Type, AiFoundryJobTypes.BatchReplan, StringComparison.OrdinalIgnoreCase), ct);
        }

        if (string.Equals(completedJob.Type, AiFoundryJobTypes.BriefGenerate, StringComparison.OrdinalIgnoreCase))
        {
            await PersistTaskBriefAndEnqueueBriefReviewAsync(completedJob, ct);
        }

        if (string.Equals(completedJob.Type, AiFoundryJobTypes.BriefReview, StringComparison.OrdinalIgnoreCase))
        {
            await PersistBriefReviewAndContinueAsync(completedJob, ct);
        }

        if (string.Equals(completedJob.Type, AiFoundryJobTypes.BriefRepair, StringComparison.OrdinalIgnoreCase))
        {
            await PersistBriefRepairAndRequeueReviewAsync(completedJob, ct);
        }

        if (string.Equals(completedJob.Type, AiFoundryJobTypes.ReferencePackBuild, StringComparison.OrdinalIgnoreCase))
        {
            await PersistReferencePackAndEnqueueDraftGenerationAsync(completedJob, ct);
        }

        if (string.Equals(completedJob.Type, AiFoundryJobTypes.StudentJourneyReview, StringComparison.OrdinalIgnoreCase))
        {
            await PersistStudentJourneyReviewAsync(completedJob, ct);
            if (completedJob.TargetEntityId != null)
            {
                await RefreshBatchSummaryAsync(completedJob.TargetEntityId.Value, ct);
                await MaybeEnqueueBatchPublishPrepareAsync(completedJob.TargetEntityId.Value, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
            }
        }

        if (string.Equals(completedJob.Type, AiFoundryJobTypes.BatchPublishPrepare, StringComparison.OrdinalIgnoreCase))
        {
            await PersistBatchPublishPrepareAsync(completedJob, ct);
            if (completedJob.TargetEntityId != null)
            {
                await RefreshBatchSummaryAsync(completedJob.TargetEntityId.Value, ct);
                await MaybeEnqueuePlannerFeedbackAsync(completedJob.TargetEntityId.Value, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
            }
        }

        if (string.Equals(completedJob.Type, AiFoundryJobTypes.PlannerFeedback, StringComparison.OrdinalIgnoreCase))
        {
            await PersistPlannerFeedbackAsync(completedJob, ct);
            if (completedJob.TargetEntityId != null)
                await RefreshBatchSummaryAsync(completedJob.TargetEntityId.Value, ct);
        }

        if (completedJob.Type.StartsWith("assignment_generate", StringComparison.OrdinalIgnoreCase))
        {
            var draft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.JobId == completedJob.Id, ct);
            if (draft != null)
            {
                if (string.Equals(completedJob.TargetEntityType, "ai-batch-item", StringComparison.OrdinalIgnoreCase) && completedJob.TargetEntityId != null)
                {
                    var item = await _db.AiBatchItems.FirstOrDefaultAsync(x => x.Id == completedJob.TargetEntityId.Value, ct);
                    if (item != null)
                    {
                        item.DraftId = draft.Id;
                        item.Status = draft.Status;
                        item.UpdatedAtUtc = DateTime.UtcNow;
                    }
                }
                await EnsureDraftReviewJobsAsync(completedJob, draft, ct);
            }
            else if (string.Equals(completedJob.TargetEntityType, "ai-batch-item", StringComparison.OrdinalIgnoreCase) && completedJob.TargetEntityId != null)
            {
                var item = await _db.AiBatchItems.Include(x => x.Batch).FirstOrDefaultAsync(x => x.Id == completedJob.TargetEntityId.Value, ct);
                if (item != null)
                {
                    item.Status = "draft-missing";
                    item.UpdatedAtUtc = DateTime.UtcNow;
                    item.Batch.Status = "drafting";
                    item.Batch.CurrentStage = AiFoundryStages.DraftGenerate;
                    item.Batch.UpdatedAtUtc = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    await CreateBatchDecisionLogAsync(item.BatchId, item.Id, completedJob.Id, AiFoundryStages.DraftGenerate, "draft-missing", "Draft generation завершилась без draft JSON. Требуется repair/fallback на уровне worker или повторный прогон stage.", completedJob.ResultJson, ct);
                }
            }
        }

        if (string.Equals(completedJob.Type, AiFoundryJobTypes.StructuralReview, StringComparison.OrdinalIgnoreCase)
            || string.Equals(completedJob.Type, AiFoundryJobTypes.PedagogyReview, StringComparison.OrdinalIgnoreCase)
            || string.Equals(completedJob.Type, AiFoundryJobTypes.RuntimeReview, StringComparison.OrdinalIgnoreCase)
            || string.Equals(completedJob.Type, AiFoundryJobTypes.SimilarityReview, StringComparison.OrdinalIgnoreCase)
            || string.Equals(completedJob.Type, AiFoundryJobTypes.StyleReview, StringComparison.OrdinalIgnoreCase)
            || string.Equals(completedJob.Type, AiFoundryJobTypes.BatchContextReview, StringComparison.OrdinalIgnoreCase)
            || string.Equals(completedJob.Type, AiFoundryJobTypes.TestStrengthReview, StringComparison.OrdinalIgnoreCase)
            || string.Equals(completedJob.Type, AiFoundryJobTypes.BatchReview, StringComparison.OrdinalIgnoreCase))
        {
            await PersistDraftReviewArtifactAsync(completedJob, ct);
            await PersistReviewFindingsAsync(completedJob, ct);
            if (string.Equals(completedJob.Type, AiFoundryJobTypes.BatchReview, StringComparison.OrdinalIgnoreCase))
            {
                await PersistBatchReviewAsync(completedJob, ct);
                if (completedJob.TargetEntityId != null)
                {
                    await RefreshBatchSummaryAsync(completedJob.TargetEntityId.Value, ct);
                    await MaybeEnqueueStudentJourneyReviewAsync(completedJob.TargetEntityId.Value, completedJob.CreatedByUserId, completedJob.CreatedByDisplayName, ct);
                }
            }
            else
            {
                var draftId = await RefreshDraftScorecardAsync(completedJob, ct);
                await TryScheduleRepairAfterReviewsAsync(completedJob, ct);
                if (draftId != null)
                    await RefreshDraftScorecardAsync(draftId.Value, ct);
            }
        }

        if (string.Equals(completedJob.Type, AiFoundryJobTypes.Repair, StringComparison.OrdinalIgnoreCase))
        {
            await PersistDraftRepairArtifactAsync(completedJob, ct);
            await MarkDraftAndBatchItemRepairedAsync(completedJob, ct);
            await EnqueuePostRepairReviewsAsync(completedJob, ct);
        }

        // ── Batch → Chat feedback: push status updates into originating chat session ──
        await TryPushBatchStatusToChatAsync(completedJob, ct);

        ConsoleFoundry("sync-finish", completedJob);
    }

    private async Task EnsureDraftReviewJobsAsync(AiJob parentJob, AiGeneratedAssignmentDraft draft, CancellationToken ct)
    {
        ConsoleFoundryDraft("ensure-review-jobs-start", draft, $"parentJobId='{parentJob.Id}'");
        var expectedTypes = GetExpectedDraftReviewTypes(draft);
        var existingTypes = await _db.AiJobs
            .Where(x => x.ParentJobId == parentJob.Id && x.TargetEntityId == draft.Id && expectedTypes.Contains(x.Type))
            .Select(x => x.Type)
            .ToListAsync(ct);
        if (expectedTypes.All(t => existingTypes.Contains(t)))
            return;

        JsonDocument? parsed = null;
        try { parsed = JsonDocument.Parse(draft.DraftJson); } catch { }
        var draftPayload = parsed != null ? JsonSerializer.Deserialize<object>(parsed.RootElement.GetRawText()) : null;

        object? referenceAssignments = null;
        if (!string.IsNullOrWhiteSpace(parentJob.InputJson))
        {
            try
            {
                using var parentDoc = JsonDocument.Parse(parentJob.InputJson);
                if (parentDoc.RootElement.TryGetProperty("referenceAssignments", out var refsNode))
                    referenceAssignments = JsonSerializer.Deserialize<object>(refsNode.GetRawText());
            }
            catch { }
        }

        object? batchPeerDrafts = null;
        object? batchItemContext = null;
        object? batchPlan = null;
        if (draft.BatchId != null && draft.BatchItemId != null)
        {
            var peerDraftRows = await _db.AiGeneratedAssignmentDrafts.AsNoTracking()
                .Where(x => x.BatchId == draft.BatchId && x.BatchItemId != null && x.BatchItemId != draft.BatchItemId)
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => new { id = x.Id, batchItemId = x.BatchItemId, title = x.Title, draftJson = x.DraftJson, status = x.Status })
                .Take(20)
                .ToListAsync(ct);
            batchPeerDrafts = peerDraftRows.Select(x => new { x.id, x.batchItemId, x.title, descriptionSummary = ExtractDescriptionSummaryFromDraftJson(x.draftJson), x.status }).ToList();

            var itemRow = await _db.AiBatchItems.AsNoTracking()
                .Where(x => x.Id == draft.BatchItemId.Value)
                .Select(x => new
                {
                    x.Id,
                    x.Index,
                    x.TargetSkill,
                    x.DifficultyTarget,
                    x.MicroGoal,
                    x.Status,
                    x.ScorecardJson,
                    PlanJson = x.Batch.PlanJson,
                })
                .FirstOrDefaultAsync(ct);
            if (itemRow != null)
            {
                batchItemContext = new { itemRow.Id, itemRow.Index, itemRow.TargetSkill, itemRow.DifficultyTarget, itemRow.MicroGoal, itemRow.Status, scorecard = TryDeserializeJsonObject(itemRow.ScorecardJson) };
                batchPlan = TryDeserializeJsonObject(itemRow.PlanJson);
            }
        }

        var commonInput = new
        {
            requestType = "assignment_foundry_review",
            draftId = draft.Id,
            assignmentType = draft.AssignmentType,
            courseId = draft.CourseId,
            title = draft.Title,
            draft = draftPayload,
            batchId = draft.BatchId,
            batchItemId = draft.BatchItemId,
            batchPeerDrafts,
            batchItemContext,
            batchPlan,
            referenceAssignments,
        };

        async Task enqueue(string type, string stageCode, string label, int order)
        {
            if (existingTypes.Contains(type)) return;
            await EnqueueAsync(new CreateAiJobRequestDto
            {
                Type = type,
                ParentJobId = parentJob.Id,
                StageCode = stageCode,
                StageLabel = label,
                StageOrder = order,
                TargetEntityType = "ai-draft",
                TargetEntityId = draft.Id,
                CourseId = draft.CourseId,
                Priority = Math.Max(parentJob.Priority - 1, 1),
                InputJson = JsonSerializer.Serialize(commonInput, JsonOptions),
            }, parentJob.CreatedByUserId, parentJob.CreatedByDisplayName, ct);
        }

        await enqueue(AiFoundryJobTypes.StructuralReview, AiFoundryStages.StructuralReview, "AI structural review", 20);
        await enqueue(AiFoundryJobTypes.PedagogyReview, AiFoundryStages.PedagogyReview, "AI pedagogy review", 30);
        await enqueue(AiFoundryJobTypes.StyleReview, AiFoundryStages.StyleReview, "AI style review", 35);
        await enqueue(AiFoundryJobTypes.SimilarityReview, AiFoundryStages.SimilarityReview, "AI similarity review", 40);
        await enqueue(AiFoundryJobTypes.BatchContextReview, AiFoundryStages.BatchContextReview, "AI batch context review", 45);
        await enqueue(AiFoundryJobTypes.TestStrengthReview, AiFoundryStages.TestStrengthReview, "AI test strength review", 50);
        await enqueue(AiFoundryJobTypes.RuntimeReview, AiFoundryStages.RuntimeReview, "AI runtime review", 60);
    }

    private static List<string> GetExpectedDraftReviewTypes(AiGeneratedAssignmentDraft draft)
    {
        var types = new List<string>
        {
            AiFoundryJobTypes.StructuralReview,
            AiFoundryJobTypes.PedagogyReview,
            AiFoundryJobTypes.StyleReview,
            AiFoundryJobTypes.SimilarityReview,
            AiFoundryJobTypes.TestStrengthReview,
            AiFoundryJobTypes.RuntimeReview,
        };
        if (draft.BatchId != null && draft.BatchItemId != null)
            types.Add(AiFoundryJobTypes.BatchContextReview);
        return types;
    }

    private async Task PersistDraftReviewArtifactAsync(AiJob reviewJob, CancellationToken ct)
    {
        ConsoleFoundry("persist-review-artifact-start", reviewJob);
        if (await _db.AiArtifacts.AnyAsync(x => x.JobId == reviewJob.Id && x.ArtifactType == "draft-review", ct))
        {
            return;
        }

        var draftId = reviewJob.TargetEntityId;
        if (draftId == null && !string.IsNullOrWhiteSpace(reviewJob.InputJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(reviewJob.InputJson);
                draftId = ExtractGuid(doc.RootElement, "draftId");
            }
            catch
            {
            }
        }

        AiGeneratedAssignmentDraft? draft = null;
        if (draftId != null)
        {
            draft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.Id == draftId.Value, ct);
        }

        string? status = null;
        if (!string.IsNullOrWhiteSpace(reviewJob.ResultJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(reviewJob.ResultJson);
                if (doc.RootElement.TryGetProperty("status", out var statusNode) && statusNode.ValueKind == JsonValueKind.String)
                    status = statusNode.GetString();
            }
            catch
            {
            }
        }

        _db.AiArtifacts.Add(new AiArtifact
        {
            Id = Guid.NewGuid(),
            JobId = reviewJob.Id,
            DraftId = draft?.Id,
            ArtifactType = "draft-review",
            StageCode = reviewJob.StageCode,
            Status = string.IsNullOrWhiteSpace(status) ? reviewJob.Status : status,
            PayloadJson = reviewJob.ResultJson,
            ModelName = reviewJob.ModelName,
            CreatedAtUtc = DateTime.UtcNow,
        });

        if (draft != null)
        {
            draft.DraftJson = MergeDraftReview(draft.DraftJson, reviewJob.StageCode ?? reviewJob.Type, reviewJob.ResultJson);
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                draft.Status = "needs-fix";
            else if (string.Equals(status, "needs-review", StringComparison.OrdinalIgnoreCase) && draft.Status == "ready")
                draft.Status = "needs-review";
            draft.UpdatedAtUtc = DateTime.UtcNow;

            if (string.Equals(reviewJob.Type, AiFoundryJobTypes.BatchContextReview, StringComparison.OrdinalIgnoreCase) && draft.BatchItemId != null)
            {
                var item = _db.AiBatchItems.FirstOrDefault(x => x.Id == draft.BatchItemId.Value);
                if (item != null)
                {
                    item.ContextReviewJson = reviewJob.ResultJson;
                    item.UpdatedAtUtc = DateTime.UtcNow;
                }
            }
        }
    }

    private static string MergeDraftReview(string? existingDraftJson, string reviewKey, string? reviewJson)
    {
        JsonNode rootNode;
        try
        {
            rootNode = string.IsNullOrWhiteSpace(existingDraftJson)
                ? new JsonObject()
                : JsonNode.Parse(existingDraftJson!) ?? new JsonObject();
        }
        catch
        {
            rootNode = new JsonObject();
        }

        if (rootNode is not JsonObject rootObj)
            rootObj = new JsonObject();

        // Detach "meta" from rootObj before reparenting to avoid
        // "The node already has a parent" when meta is already a child.
        var meta = rootObj["meta"] as JsonObject;
        if (meta != null)
            rootObj.Remove("meta");
        else
            meta = new JsonObject();

        var reviews = meta["aiReviews"] as JsonObject;
        if (reviews != null)
            meta.Remove("aiReviews");
        else
            reviews = new JsonObject();

        reviews[reviewKey] = string.IsNullOrWhiteSpace(reviewJson) ? new JsonObject() : JsonNode.Parse(reviewJson!);
        meta["aiReviews"] = reviews;
        rootObj["meta"] = meta;
        return rootObj.ToJsonString(JsonOptions);
    }


    private async Task TryScheduleRepairAfterReviewsAsync(AiJob reviewJob, CancellationToken ct)
    {
        ConsoleFoundry("repair-routing-start", reviewJob);
        var draftId = reviewJob.TargetEntityId;
        if (draftId == null && !string.IsNullOrWhiteSpace(reviewJob.InputJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(reviewJob.InputJson);
                draftId = ExtractGuid(doc.RootElement, "draftId");
            }
            catch
            {
            }
        }
        if (draftId == null)
            return;

        var draft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.Id == draftId.Value, ct);
        if (draft == null)
            return;

        var reviewParentId = reviewJob.ParentJobId;
        if (reviewParentId == null)
            return;

        var siblingReviews = await _db.AiJobs
            .Where(x => x.ParentJobId == reviewParentId
                && x.TargetEntityId == draftId
                && (x.Type == AiFoundryJobTypes.StructuralReview || x.Type == AiFoundryJobTypes.PedagogyReview || x.Type == AiFoundryJobTypes.StyleReview || x.Type == AiFoundryJobTypes.RuntimeReview || x.Type == AiFoundryJobTypes.SimilarityReview || x.Type == AiFoundryJobTypes.TestStrengthReview || x.Type == AiFoundryJobTypes.BatchContextReview))
            .OrderBy(x => x.StageOrder)
            .ThenBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        var expectedReviewTypes = GetExpectedDraftReviewTypes(draft);
        if (expectedReviewTypes.Any(t => !siblingReviews.Any(x => x.Type == t)) || siblingReviews.Any(x => x.Status != "done"))
            return;

        var repairCount = await _db.AiJobs.CountAsync(x => x.Type == AiFoundryJobTypes.Repair && x.TargetEntityId == draftId, ct);
        if (repairCount >= 1)
            return;

        var existingRepairPending = await _db.AiJobs.AnyAsync(x => x.Type == AiFoundryJobTypes.Repair && x.TargetEntityId == draftId && (x.Status == "pending" || x.Status == "processing" || x.Status == "retry"), ct);
        if (existingRepairPending)
            return;

        var scorecardJson = await RefreshDraftScorecardAsync(draft.Id, ct);
        var routing = BuildRepairRoutingPlan(siblingReviews, scorecardJson);

        if (!routing.NeedsRepair)
        {
            var draftStatus = IsFallbackDraftJson(draft.DraftJson)
                ? "fallback-review"
                : (routing.PublishRecommendation == "ready" ? "ready" : "reviewed");
            draft.Status = draftStatus;
            draft.UpdatedAtUtc = DateTime.UtcNow;
            if (draft.BatchItemId != null)
            {
                var batchItem = await _db.AiBatchItems.FirstOrDefaultAsync(x => x.Id == draft.BatchItemId.Value, ct);
                if (batchItem != null)
                {
                    batchItem.Status = draftStatus;
                    batchItem.ScorecardJson = scorecardJson;
                    batchItem.UpdatedAtUtc = DateTime.UtcNow;
                }
            }
            await _db.SaveChangesAsync(ct);
            if (draft.BatchId != null)
                await RefreshBatchSummaryAsync(draft.BatchId.Value, ct);
            if (draft.BatchId != null)
                await MaybeEnqueueBatchReviewAsync(draft.BatchId.Value, reviewJob.CreatedByUserId, reviewJob.CreatedByDisplayName, ct);
            return;
        }

        object? draftPayload = null;
        try
        {
            draftPayload = JsonSerializer.Deserialize<object>(draft.DraftJson);
        }
        catch
        {
        }

        object? referenceAssignments = null;
        var referenceCarrier = siblingReviews.FirstOrDefault();
        if (referenceCarrier != null && !string.IsNullOrWhiteSpace(referenceCarrier.InputJson))
        {
            try
            {
                using var inputDoc = JsonDocument.Parse(referenceCarrier.InputJson);
                if (inputDoc.RootElement.TryGetProperty("referenceAssignments", out var refsNode))
                {
                    referenceAssignments = JsonSerializer.Deserialize<object>(refsNode.GetRawText());
                }
            }
            catch
            {
            }
        }

        var reviewResults = siblingReviews
            .Where(x => !string.IsNullOrWhiteSpace(x.ResultJson))
            .Select(x => new
            {
                type = x.Type,
                stageCode = x.StageCode,
                status = ExtractStringProperty(x.ResultJson, "status"),
                score = ExtractDoubleProperty(x.ResultJson, "score"),
                result = TryDeserializeJsonObject(x.ResultJson),
            })
            .ToList();

        var item = draft.BatchItemId != null ? await _db.AiBatchItems.Include(x => x.Batch).FirstOrDefaultAsync(x => x.Id == draft.BatchItemId.Value, ct) : null;
        var shouldRouteToBrief = item != null && (routing.PrimaryRoute == "brief" || routing.PublishRecommendation == "rewrite-brief" || routing.PublishRecommendation == "regenerate-from-brief");

        if (shouldRouteToBrief)
        {
            var existingBriefRepairs = await _db.AiJobs.CountAsync(x => x.Type == AiFoundryJobTypes.BriefRepair && x.TargetEntityId == item!.Id, ct);
            var pendingBriefRepair = await _db.AiJobs.AnyAsync(x => x.Type == AiFoundryJobTypes.BriefRepair && x.TargetEntityId == item.Id && (x.Status == "pending" || x.Status == "processing" || x.Status == "retry"), ct);
            if (!pendingBriefRepair && existingBriefRepairs < 2)
            {
                item.RepairCount += 1;
                item.Status = "brief-repair";
                item.UpdatedAtUtc = DateTime.UtcNow;
                item.Batch.Status = "brief-repair";
                item.Batch.CurrentStage = AiFoundryStages.BriefRepair;
                item.Batch.UpdatedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                await EnqueueAsync(new CreateAiJobRequestDto
                {
                    Type = AiFoundryJobTypes.BriefRepair,
                    ParentJobId = reviewParentId,
                    TargetEntityType = "ai-batch-item",
                    TargetEntityId = item.Id,
                    CourseId = item.Batch.CourseId,
                    Priority = Math.Max(reviewJob.Priority - 1, 1),
                    StageCode = AiFoundryStages.BriefRepair,
                    StageLabel = "AI routed brief repair",
                    StageOrder = 41,
                    InputJson = JsonSerializer.Serialize(new
                    {
                        requestType = "assignment_brief_repair",
                        batchId = item.BatchId,
                        batchItemId = item.Id,
                        courseId = item.Batch.CourseId,
                        assignmentType = item.Batch.AssignmentType,
                        prompt = item.Batch.Prompt,
                        brief = TryDeserializeJsonObject(item.BriefJson),
                        briefReview = TryDeserializeJsonObject(item.BriefReviewJson),
                        reviewResults,
                        scorecard = TryDeserializeJsonObject(scorecardJson),
                        repairPlan = routing.ToPayload(),
                        plan = TryDeserializeJsonObject(item.Batch.PlanJson),
                        courseProfile = TryDeserializeJsonObject(item.Batch.CourseProfileJson),
                        gapAnalysis = TryDeserializeJsonObject(item.Batch.GapAnalysisJson),
                        referenceAssignments,
                        repairCount = item.RepairCount,
                    }, JsonOptions),
                }, reviewJob.CreatedByUserId, reviewJob.CreatedByDisplayName, ct);

                await CreateBatchDecisionLogAsync(item.BatchId, item.Id, reviewJob.Id, AiFoundryStages.BriefRepair, "brief-repair-routed", $"Draft routed to brief repair ({routing.PrimaryRoute}).", scorecardJson, ct);
                await RefreshBatchSummaryAsync(item.BatchId, ct);
                return;
            }
        }

        await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = AiFoundryJobTypes.Repair,
            ParentJobId = reviewParentId,
            StageCode = AiFoundryStages.Repair,
            StageLabel = $"AI repair loop [{routing.PrimaryRoute}]",
            StageOrder = 40,
            TargetEntityType = "ai-draft",
            TargetEntityId = draft.Id,
            CourseId = draft.CourseId,
            Priority = Math.Max(reviewJob.Priority - 1, 1),
            InputJson = JsonSerializer.Serialize(new
            {
                requestType = "assignment_foundry_repair",
                draftId = draft.Id,
                assignmentType = draft.AssignmentType,
                courseId = draft.CourseId,
                draft = draftPayload,
                reviewResults,
                referenceAssignments,
                repairCount,
                scorecard = TryDeserializeJsonObject(scorecardJson),
                repairPlan = routing.ToPayload(),
            }, JsonOptions),
        }, reviewJob.CreatedByUserId, reviewJob.CreatedByDisplayName, ct);

        if (item != null)
        {
            item.RepairCount += 1;
            item.Status = $"repair-{routing.PrimaryRoute}";
            item.UpdatedAtUtc = DateTime.UtcNow;
            item.ScorecardJson = scorecardJson;
            await _db.SaveChangesAsync(ct);
            await CreateBatchDecisionLogAsync(item.BatchId, item.Id, reviewJob.Id, AiFoundryStages.Repair, "draft-repair-routed", $"Draft routed to {routing.PrimaryRoute} repair.", scorecardJson, ct);
            await RefreshBatchSummaryAsync(item.BatchId, ct);
        }
    }

    private async Task PersistDraftRepairArtifactAsync(AiJob repairJob, CancellationToken ct)
    {
        ConsoleFoundry("persist-draft-repair-artifact-start", repairJob);
        if (await _db.AiArtifacts.AnyAsync(x => x.JobId == repairJob.Id && x.ArtifactType == "draft-repair", ct))
            return;

        var draftId = repairJob.TargetEntityId;
        if (draftId == null && !string.IsNullOrWhiteSpace(repairJob.InputJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(repairJob.InputJson);
                draftId = ExtractGuid(doc.RootElement, "draftId");
            }
            catch
            {
            }
        }

        _db.AiArtifacts.Add(new AiArtifact
        {
            Id = Guid.NewGuid(),
            JobId = repairJob.Id,
            DraftId = draftId,
            ArtifactType = "draft-repair",
            StageCode = repairJob.StageCode,
            Status = repairJob.Status,
            PayloadJson = repairJob.ResultJson,
            ModelName = repairJob.ModelName,
            CreatedAtUtc = DateTime.UtcNow,
        });
    }

    private async Task EnqueuePostRepairReviewsAsync(AiJob repairJob, CancellationToken ct)
    {
        ConsoleFoundry("post-repair-review-enqueue-start", repairJob);
        var draftId = repairJob.TargetEntityId;
        if (draftId == null)
            return;

        var draft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.Id == draftId.Value, ct);
        if (draft == null)
            return;

        await EnsureDraftReviewJobsAsync(repairJob, draft, ct);
    }

    private async Task PersistReviewFindingsAsync(AiJob reviewJob, CancellationToken ct)
    {
        ConsoleFoundry("persist-review-findings-start", reviewJob);
        if (string.IsNullOrWhiteSpace(reviewJob.ResultJson))
            return;

        var artifact = await _db.AiArtifacts.FirstOrDefaultAsync(x => x.JobId == reviewJob.Id && x.ArtifactType == "draft-review", ct);
        if (artifact == null)
            return;

        if (await _db.AiReviewFindings.AnyAsync(x => x.ArtifactId == artifact.Id, ct))
            return;

        try
        {
            using var doc = JsonDocument.Parse(reviewJob.ResultJson);
            if (!doc.RootElement.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
                return;
            foreach (var finding in findings.EnumerateArray())
            {
                _db.AiReviewFindings.Add(new AiReviewFinding
                {
                    Id = Guid.NewGuid(),
                    ArtifactId = artifact.Id,
                    DraftId = artifact.DraftId,
                    StageCode = reviewJob.StageCode ?? reviewJob.Type,
                    Severity = finding.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.String ? sev.GetString() : null,
                    Code = finding.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null,
                    Message = finding.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() ?? "review finding" : "review finding",
                    SuggestedRepair = finding.TryGetProperty("suggestedRepair", out var rep) && rep.ValueKind == JsonValueKind.String ? rep.GetString() : null,
                    Confidence = finding.TryGetProperty("confidence", out var conf) && conf.TryGetDouble(out var v) ? v : null,
                    CreatedAtUtc = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }


    private async Task<Guid?> RefreshDraftScorecardAsync(AiJob reviewJob, CancellationToken ct)
    {
        ConsoleFoundry("scorecard-refresh-from-job-start", reviewJob);
        var draftId = reviewJob.TargetEntityId;
        if (draftId == null && !string.IsNullOrWhiteSpace(reviewJob.InputJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(reviewJob.InputJson);
                draftId = ExtractGuid(doc.RootElement, "draftId");
            }
            catch { }
        }
        if (draftId == null) return null;
        await RefreshDraftScorecardAsync(draftId.Value, ct);
        return draftId.Value;
    }

    private async Task<string?> RefreshDraftScorecardAsync(Guid draftId, CancellationToken ct)
    {
        Console.WriteLine($"[AiJobService][Foundry] scorecard-refresh >>> draftId={draftId}");
        var draft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.Id == draftId, ct);
        if (draft == null)
            return null;

        var reviews = await _db.AiJobs.AsNoTracking()
            .Where(x => x.TargetEntityId == draftId && (x.Type == AiFoundryJobTypes.StructuralReview || x.Type == AiFoundryJobTypes.PedagogyReview || x.Type == AiFoundryJobTypes.StyleReview || x.Type == AiFoundryJobTypes.RuntimeReview || x.Type == AiFoundryJobTypes.SimilarityReview || x.Type == AiFoundryJobTypes.TestStrengthReview || x.Type == AiFoundryJobTypes.BatchContextReview) && x.Status == "done")
            .OrderByDescending(x => x.CompletedAtUtc ?? x.CreatedAtUtc)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        var latestByType = reviews
            .GroupBy(x => x.Type)
            .Select(x => x.First())
            .ToList();

        var scorecardJson = BuildScorecardJson(latestByType, draft.BatchItemId != null);
        scorecardJson = await EnrichScorecardWithTelemetryAsync(scorecardJson, latestByType.Select(x => x.Id).ToList(), ct);
        draft.DraftJson = MergeDraftMetaJson(draft.DraftJson, "aiScorecard", scorecardJson);
        draft.UpdatedAtUtc = DateTime.UtcNow;

        if (draft.BatchItemId != null)
        {
            var item = await _db.AiBatchItems.Include(x => x.Batch).FirstOrDefaultAsync(x => x.Id == draft.BatchItemId.Value, ct);
            if (item != null)
            {
                item.ScorecardJson = scorecardJson;
                item.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync(ct);
        if (draft.BatchId != null)
            await RefreshBatchSummaryAsync(draft.BatchId.Value, ct);
        ConsoleFoundryDraft("scorecard-refresh-done", draft, $"reviews={reviews.Count} scorecardJsonLen={scorecardJson?.Length ?? 0}");
        return scorecardJson;
    }

    private async Task<string> EnrichScorecardWithTelemetryAsync(string scorecardJson, IReadOnlyList<Guid> reviewJobIds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scorecardJson) || reviewJobIds.Count == 0)
            return scorecardJson;

        var artifacts = await _db.AiArtifacts.AsNoTracking()
            .Where(x => reviewJobIds.Contains(x.JobId) && (x.ArtifactType == "worker-telemetry" || x.ArtifactType == "selection-telemetry" || x.ArtifactType == "duplicate-clusters"))
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectionAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double totalCost = 0;
        int promptTokens = 0;
        int completionTokens = 0;
        int totalTokens = 0;
        int selectionArtifacts = 0;
        int duplicateClusterArtifacts = 0;
        int largestDuplicateCluster = 0;

        foreach (var artifact in artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.PayloadJson))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(artifact.PayloadJson);
                var root = doc.RootElement;
                if (artifact.ArtifactType == "worker-telemetry")
                {
                    var llm = root.TryGetProperty("llm", out var llmNode) && llmNode.ValueKind == JsonValueKind.Object ? llmNode : root;
                    if (llm.TryGetProperty("provider", out var providerNode) && providerNode.ValueKind == JsonValueKind.String)
                        providers.Add(providerNode.GetString() ?? string.Empty);
                    if (llm.TryGetProperty("model", out var modelNode) && modelNode.ValueKind == JsonValueKind.String)
                        models.Add(modelNode.GetString() ?? string.Empty);
                    if (llm.TryGetProperty("promptTokens", out var pt) && pt.TryGetInt32(out var ptValue))
                        promptTokens += ptValue;
                    if (llm.TryGetProperty("completionTokens", out var ctNode) && ctNode.TryGetInt32(out var ctValue))
                        completionTokens += ctValue;
                    if (llm.TryGetProperty("totalTokens", out var ttNode) && ttNode.TryGetInt32(out var ttValue))
                        totalTokens += ttValue;
                    if (llm.TryGetProperty("cost", out var costNode) && costNode.TryGetDouble(out var costValue))
                        totalCost += costValue;
                    if (root.TryGetProperty("selectionTelemetry", out var selectionNode) && selectionNode.ValueKind == JsonValueKind.Object && selectionNode.TryGetProperty("anchorId", out var anchorNode) && anchorNode.ValueKind == JsonValueKind.String)
                        selectionAnchors.Add(anchorNode.GetString() ?? string.Empty);
                }
                else if (artifact.ArtifactType == "selection-telemetry")
                {
                    selectionArtifacts += 1;
                    if (root.TryGetProperty("anchorId", out var anchorNode) && anchorNode.ValueKind == JsonValueKind.String)
                        selectionAnchors.Add(anchorNode.GetString() ?? string.Empty);
                }
                else if (artifact.ArtifactType == "duplicate-clusters")
                {
                    duplicateClusterArtifacts += 1;
                    if (root.TryGetProperty("clusters", out var clustersNode) && clustersNode.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var cluster in clustersNode.EnumerateArray())
                        {
                            if (cluster.ValueKind == JsonValueKind.Object && cluster.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt32(out var sizeValue))
                            {
                                largestDuplicateCluster = Math.Max(largestDuplicateCluster, sizeValue);
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        using var scorecardDoc = JsonDocument.Parse(scorecardJson);
        var rootObject = JsonNode.Parse(scorecardDoc.RootElement.GetRawText())?.AsObject() ?? new JsonObject();
        rootObject["llmTelemetry"] = new JsonObject
        {
            ["artifactCount"] = artifacts.Count,
            ["providers"] = new JsonArray(providers.OrderBy(x => x).Select(x => (JsonNode?)x).ToArray()),
            ["models"] = new JsonArray(models.OrderBy(x => x).Select(x => (JsonNode?)x).ToArray()),
            ["promptTokens"] = promptTokens,
            ["completionTokens"] = completionTokens,
            ["totalTokens"] = totalTokens,
            ["estimatedCost"] = Math.Round(totalCost, 6),
            ["selectionArtifacts"] = selectionArtifacts,
            ["selectionAnchors"] = new JsonArray(selectionAnchors.OrderBy(x => x).Select(x => (JsonNode?)x).ToArray()),
            ["duplicateClusterArtifacts"] = duplicateClusterArtifacts,
            ["largestDuplicateCluster"] = largestDuplicateCluster,
        };
        return rootObject.ToJsonString(JsonOptions);
    }

    private async Task RefreshBatchSummaryAsync(Guid batchId, CancellationToken ct)
    {
        Console.WriteLine($"[AiJobService][Foundry] batch-summary-refresh >>> batchId={batchId}");
        var batch = await _db.AiBatches.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == batchId, ct);
        if (batch == null)
            return;

        var itemSummaries = new List<(string? Band, int? OverallScore, Dictionary<string, int> Dimensions, string? PrimaryRoute, string Status)>();
        foreach (var item in batch.Items)
        {
            itemSummaries.Add(ParseBatchItemSummary(item));
        }

        var overallScores = itemSummaries.Where(x => x.OverallScore != null).Select(x => x.OverallScore!.Value).ToList();
        var averageScore = overallScores.Count > 0 ? (int)Math.Round(overallScores.Average(), MidpointRounding.AwayFromZero) : 0;
        var routeCounts = itemSummaries
            .Where(x => !string.IsNullOrWhiteSpace(x.PrimaryRoute))
            .GroupBy(x => x.PrimaryRoute!)
            .ToDictionary(x => x.Key, x => x.Count());
        var bandCounts = itemSummaries
            .Where(x => !string.IsNullOrWhiteSpace(x.Band))
            .GroupBy(x => x.Band!)
            .ToDictionary(x => x.Key, x => x.Count());
        var weakDimensions = itemSummaries
            .SelectMany(x => x.Dimensions)
            .GroupBy(x => x.Key)
            .Select(x => new { key = x.Key, score = (int)Math.Round(x.Average(v => v.Value), MidpointRounding.AwayFromZero) })
            .OrderBy(x => x.score)
            .Take(4)
            .ToList();

        var batchReviewScore = ExtractDoubleProperty(batch.BatchReviewJson, "score");
        var studentJourneyScore = ExtractDoubleProperty(batch.StudentJourneyJson, "score");
        var publicationScore = ExtractDoubleProperty(batch.PublicationAuditJson, "score");

        double weighted = 0;
        double usedWeight = 0;
        if (overallScores.Count > 0)
        {
            weighted += averageScore * 0.65;
            usedWeight += 0.65;
        }
        if (batchReviewScore != null)
        {
            weighted += batchReviewScore.Value * 100 * 0.15;
            usedWeight += 0.15;
        }
        if (studentJourneyScore != null)
        {
            weighted += studentJourneyScore.Value * 100 * 0.10;
            usedWeight += 0.10;
        }
        if (publicationScore != null)
        {
            weighted += publicationScore.Value * 100 * 0.10;
            usedWeight += 0.10;
        }

        var batchOverallScore = usedWeight > 0 ? (int)Math.Round(weighted / usedWeight, MidpointRounding.AwayFromZero) : 0;
        var publicationReadiness = ExtractBatchPublicationReadiness(batch.PublicationAuditJson);
        var readiness = !string.IsNullOrWhiteSpace(publicationReadiness)
            ? publicationReadiness
            : batchOverallScore >= 90 && !routeCounts.Any() ? "ready" : batchOverallScore >= 80 ? "light-repair" : batchOverallScore >= 65 ? "deep-repair" : batchOverallScore >= 50 ? "regenerate-from-brief" : "rewrite-brief";

        batch.PositiveMemoryJson = BuildPositiveMemoryJson(batch, itemSummaries);
        batch.BatchMemoryJson = BuildBatchMemoryJson(batch, itemSummaries, routeCounts);
        batch.DecisionLogDigestJson = await BuildDecisionLogDigestJsonAsync(batch.Id, ct);
        if (!string.IsNullOrWhiteSpace(batch.PlannerFeedbackJson))
        {
            batch.DecisionSummaryJson = ExtractJsonPropertyOrOriginal(batch.PlannerFeedbackJson, "decisionSummary", batch.DecisionSummaryJson);
            batch.InstitutionalMemoryJson = BuildInstitutionalMemoryJson(batch, batch.PlannerFeedbackJson);
            batch.AntiPatternMemoryJson = BuildAntiPatternMemoryJson(batch, batch.PlannerFeedbackJson);
        }

        batch.FeedbackLoopStateJson = BuildFeedbackLoopStateJson(batch, routeCounts, readiness);

        var summary = new
        {
            version = "wave16-final-backend",
            generatedAtUtc = DateTime.UtcNow,
            requestedCount = batch.RequestedCount,
            itemsCount = batch.Items.Count,
            readyItems = batch.Items.Count(x => x.Status == "ready" || x.Status == "reviewed"),
            needsRepairItems = batch.Items.Count(x => x.Status.StartsWith("repair-") || x.Status == "brief-repair" || x.Status == "needs-fix"),
            averageItemScore = averageScore,
            batchReviewScore = batchReviewScore != null ? (int)Math.Round(batchReviewScore.Value * 100, MidpointRounding.AwayFromZero) : (int?)null,
            studentJourneyScore = studentJourneyScore != null ? (int)Math.Round(studentJourneyScore.Value * 100, MidpointRounding.AwayFromZero) : (int?)null,
            publicationGateScore = publicationScore != null ? (int)Math.Round(publicationScore.Value * 100, MidpointRounding.AwayFromZero) : (int?)null,
            overallScore = batchOverallScore,
            readiness,
            publicationDecision = TryDeserializeJsonObject(batch.PublicationAuditJson),
            publishPack = TryDeserializeJsonObject(batch.PublishPackJson),
            qualityLedger = TryDeserializeJsonObject(batch.QualityLedgerJson),
            exportManifest = TryDeserializeJsonObject(batch.ExportManifestJson),
            decisionSummary = TryDeserializeJsonObject(batch.DecisionSummaryJson),
            plannerFeedback = TryDeserializeJsonObject(batch.PlannerFeedbackJson),
            bandCounts,
            repairRoutes = routeCounts,
            weakestDimensions = weakDimensions,
            positiveMemory = TryDeserializeJsonObject(batch.PositiveMemoryJson),
            batchMemory = TryDeserializeJsonObject(batch.BatchMemoryJson),
            institutionalMemory = TryDeserializeJsonObject(batch.InstitutionalMemoryJson),
            historicalPlannerPriors = TryDeserializeJsonObject(batch.HistoricalPlannerPriorsJson),
            antiPatternMemory = TryDeserializeJsonObject(batch.AntiPatternMemoryJson),
            replanLedger = TryDeserializeJsonObject(batch.ReplanLedgerJson),
            decisionLogDigest = TryDeserializeJsonObject(batch.DecisionLogDigestJson),
            feedbackLoopState = TryDeserializeJsonObject(batch.FeedbackLoopStateJson),
        };

        batch.SummaryJson = JsonSerializer.Serialize(summary, JsonOptions);
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ── Batch → Chat feedback loop ──────────────────────────────────────────
    // Pushes a system message into the originating chat session whenever a
    // significant batch event occurs (plan ready, drafts generated, reviews done,
    // errors, needs-clarification).

    private async Task TryPushBatchStatusToChatAsync(AiJob completedJob, CancellationToken ct)
    {
        // Only handle batch-scoped stages
        if (!string.Equals(completedJob.TargetEntityType, "ai-batch", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(completedJob.TargetEntityType, "ai-batch-item", StringComparison.OrdinalIgnoreCase))
            return;

        // Resolve batch
        Guid? batchId = null;
        if (string.Equals(completedJob.TargetEntityType, "ai-batch", StringComparison.OrdinalIgnoreCase))
            batchId = completedJob.TargetEntityId;
        else if (completedJob.TargetEntityId.HasValue)
        {
            batchId = await _db.AiBatchItems.AsNoTracking()
                .Where(x => x.Id == completedJob.TargetEntityId.Value)
                .Select(x => (Guid?)x.BatchId)
                .FirstOrDefaultAsync(ct);
        }
        if (!batchId.HasValue) return;

        var batch = await _db.AiBatches.AsNoTracking()
            .Where(x => x.Id == batchId.Value)
            .Select(x => new { x.ChatSessionId, x.Status, x.CurrentStage, x.RequestedCount,
                ReadyCount = x.Items.Count(i => i.Status == "ready" || i.Status == "reviewed"),
                FailedCount = x.Items.Count(i => i.Status == "draft-missing" || i.Status.StartsWith("repair-")),
                TotalItems = x.Items.Count })
            .FirstOrDefaultAsync(ct);
        if (batch?.ChatSessionId == null) return;

        // Determine if this is a significant event worth pushing to chat
        var notification = BuildBatchChatNotification(completedJob.Type, batch.Status, batch.CurrentStage,
            batch.RequestedCount, batch.TotalItems, batch.ReadyCount, batch.FailedCount, completedJob.ResultJson);
        if (notification == null) return;

        // Load session and append system message
        var session = await _db.Set<Data.Models.Entities.AI.AiFoundryChatSession>()
            .FirstOrDefaultAsync(x => x.Id == batch.ChatSessionId.Value, ct);
        if (session == null) return;

        var messages = DeserializeChatMessages(session.MessagesJson);
        messages.Add(new
        {
            id = Guid.NewGuid(),
            role = "system",
            content = notification.Value.Message,
            createdAtUtc = DateTime.UtcNow,
            status = notification.Value.Status,
            batchId = batchId.Value,
        });
        session.MessagesJson = JsonSerializer.Serialize(messages, JsonOptions);
        session.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        Console.WriteLine($"[AiJobService][Foundry] batch→chat >>> sessionId={batch.ChatSessionId} batchStatus={batch.Status} stage={completedJob.Type}");
    }

    private static (string Message, string Status)? BuildBatchChatNotification(string jobType, string? batchStatus, string? currentStage,
        int requestedCount, int totalItems, int readyCount, int failedCount, string? resultJson)
    {
        var type = (jobType ?? "").ToLowerInvariant();

        // Batch plan completed — the plan is ready
        if (type == AiFoundryJobTypes.BatchPlan.ToLowerInvariant() || type == AiFoundryJobTypes.BatchReplan.ToLowerInvariant())
        {
            // Check if planner returned an error
            if (!string.IsNullOrWhiteSpace(resultJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(resultJson);
                    if (doc.RootElement.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object
                        && plan.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                    {
                        return ($"[Batch] ⚠️ Планировщик не смог составить план: {err.GetString()}. Уточни запрос и я попробую снова.", "needs-clarification");
                    }
                }
                catch { /* ignore parse errors */ }
            }
            return ($"[Batch] 📋 План готов. Начинаю генерацию {requestedCount} заданий.", "batch-update");
        }

        // Individual draft generated
        if (type.StartsWith("assignment_generate"))
        {
            if (readyCount + failedCount >= totalItems && totalItems > 0)
            {
                if (failedCount > 0)
                    return ($"[Batch] ⚠️ Генерация завершена: {readyCount}/{totalItems} заданий готово, {failedCount} с проблемами. Проверь результат.", "batch-update");
                return ($"[Batch] ✅ Все {readyCount} заданий сгенерированы. Начинаю проверку качества.", "batch-update");
            }
            return null; // not all items done yet, don't spam
        }

        // Batch review done — final quality verdict
        if (type == AiFoundryJobTypes.BatchReview.ToLowerInvariant())
            return ($"[Batch] 📊 Проверка batch завершена. Готово: {readyCount}/{totalItems}. Текущий статус: {batchStatus}.", "batch-update");

        // Publication audit ready
        if (type == AiFoundryJobTypes.BatchPublishPrepare.ToLowerInvariant())
            return ($"[Batch] 📦 Аудит публикации готов. Статус: {batchStatus}.", "batch-update");

        // Planner feedback
        if (type == AiFoundryJobTypes.PlannerFeedback.ToLowerInvariant())
            return ($"[Batch] 🎯 Обратная связь от планировщика получена. Batch завершён. Статус: {batchStatus}.", "batch-update");

        return null; // Not a significant event
    }

    private static List<object> DeserializeChatMessages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<object>();
        try
        {
            return JsonSerializer.Deserialize<List<object>>(json, JsonOptions) ?? new List<object>();
        }
        catch
        {
            return new List<object>();
        }
    }

    private static (string? Band, int? OverallScore, Dictionary<string, int> Dimensions, string? PrimaryRoute, string Status) ParseBatchItemSummary(AiBatchItem item)
    {
        var dimensions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(item.ScorecardJson))
            return (null, null, dimensions, null, item.Status);
        try
        {
            using var doc = JsonDocument.Parse(item.ScorecardJson);
            var root = doc.RootElement;
            string? band = root.TryGetProperty("band", out var bandNode) && bandNode.ValueKind == JsonValueKind.String ? bandNode.GetString() : null;
            int? overall = root.TryGetProperty("overallScore", out var scoreNode) && scoreNode.TryGetInt32(out var score) ? score : null;
            string? primaryRoute = null;
            if (root.TryGetProperty("repairPlan", out var repairPlan) && repairPlan.ValueKind == JsonValueKind.Object && repairPlan.TryGetProperty("primaryRoute", out var routeNode) && routeNode.ValueKind == JsonValueKind.String)
                primaryRoute = routeNode.GetString();
            if (root.TryGetProperty("dimensions", out var dims) && dims.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in dims.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("score", out var dimScore) && dimScore.TryGetInt32(out var parsed))
                        dimensions[prop.Name] = parsed;
                }
            }
            return (band, overall, dimensions, primaryRoute, item.Status);
        }
        catch
        {
            return (null, null, dimensions, null, item.Status);
        }
    }

    private static string BuildScorecardJson(IReadOnlyList<AiJob> reviews, bool includeBatchFit)
    {
        var reviewMap = reviews.ToDictionary(x => x.Type, x => x, StringComparer.OrdinalIgnoreCase);
        var dimensionDefs = new[]
        {
            new { Key = "structuralCompleteness", Label = "Structural completeness", Weight = 0.18, Type = AiFoundryJobTypes.StructuralReview },
            new { Key = "runtimeCorrectness", Label = "Runtime correctness", Weight = 0.18, Type = AiFoundryJobTypes.RuntimeReview },
            new { Key = "testStrength", Label = "Test strength", Weight = 0.16, Type = AiFoundryJobTypes.TestStrengthReview },
            new { Key = "pedagogicalClarity", Label = "Pedagogical clarity", Weight = 0.14, Type = AiFoundryJobTypes.PedagogyReview },
            new { Key = "novelty", Label = "Novelty", Weight = 0.12, Type = AiFoundryJobTypes.SimilarityReview },
            new { Key = "courseFit", Label = "Course fit", Weight = 0.10, Type = AiFoundryJobTypes.StyleReview },
            new { Key = "batchFit", Label = "Batch fit", Weight = 0.08, Type = AiFoundryJobTypes.BatchContextReview },
        };

        var dimensions = new JsonObject();
        var reviewStatuses = new JsonObject();
        double weightedSum = 0;
        double usedWeight = 0;
        foreach (var def in dimensionDefs)
        {
            if (!includeBatchFit && def.Key == "batchFit")
                continue;
            reviewMap.TryGetValue(def.Type, out var job);
            var scorePercent = job != null ? Math.Clamp((int)Math.Round((ExtractDoubleProperty(job.ResultJson, "score") ?? 0) * 100, MidpointRounding.AwayFromZero), 0, 100) : 0;
            var status = job != null ? (ExtractStringProperty(job.ResultJson, "status") ?? job.Status) : "missing";
            if (job != null)
            {
                weightedSum += scorePercent * def.Weight;
                usedWeight += def.Weight;
                reviewStatuses[def.Type] = status;
            }
            dimensions[def.Key] = new JsonObject
            {
                ["label"] = def.Label,
                ["score"] = scorePercent,
                ["status"] = status,
                ["sourceReviewType"] = job?.Type,
                ["sourceJobId"] = job?.Id.ToString(),
            };
        }

        var policyScore = ComputePolicyScore(reviewMap.Values);
        dimensions["policyConformity"] = new JsonObject
        {
            ["label"] = "Policy conformity",
            ["score"] = policyScore,
            ["status"] = policyScore >= 90 ? "passed" : policyScore >= 75 ? "warning" : "failed",
            ["sourceReviewType"] = "derived",
        };
        weightedSum += policyScore * 0.04;
        usedWeight += 0.04;

        var findingsSummary = BuildFindingsSummary(reviewMap.Values);
        var overallScore = usedWeight > 0 ? (int)Math.Round(weightedSum / usedWeight, MidpointRounding.AwayFromZero) : 0;
        var band = overallScore >= 90 ? "ready" : overallScore >= 80 ? "light-repair" : overallScore >= 65 ? "deep-repair" : overallScore >= 50 ? "regenerate-from-brief" : "rewrite-brief";
        var repairPlan = BuildRepairPlanPayload(findingsSummary, band);

        var root = new JsonObject
        {
            ["version"] = "wave10-scorecard",
            ["generatedAtUtc"] = DateTime.UtcNow.ToString("O"),
            ["overallScore"] = overallScore,
            ["band"] = band,
            ["readyForPublish"] = band == "ready",
            ["publishRecommendation"] = band,
            ["dimensions"] = dimensions,
            ["reviewStatuses"] = reviewStatuses,
            ["findingsSummary"] = findingsSummary,
            ["repairPlan"] = repairPlan,
            ["thresholds"] = new JsonObject
            {
                ["ready"] = 90,
                ["lightRepair"] = 80,
                ["deepRepair"] = 65,
                ["regenerateFromBrief"] = 50,
            },
        };
        return root.ToJsonString(JsonOptions);
    }

    private static int ComputePolicyScore(IEnumerable<AiJob> jobs)
    {
        var score = 100;
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.ResultJson)) continue;
            try
            {
                using var doc = JsonDocument.Parse(job.ResultJson);
                if (!doc.RootElement.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var finding in findings.EnumerateArray())
                {
                    var code = finding.TryGetProperty("code", out var codeNode) && codeNode.ValueKind == JsonValueKind.String ? (codeNode.GetString() ?? string.Empty).ToLowerInvariant() : string.Empty;
                    var msg = finding.TryGetProperty("message", out var msgNode) && msgNode.ValueKind == JsonValueKind.String ? (msgNode.GetString() ?? string.Empty).ToLowerInvariant() : string.Empty;
                    var severity = finding.TryGetProperty("severity", out var sevNode) && sevNode.ValueKind == JsonValueKind.String ? (sevNode.GetString() ?? string.Empty).ToLowerInvariant() : string.Empty;
                    var hits = code.Contains("policy") || code.Contains("required") || code.Contains("forbidden") || code.Contains("language") || msg.Contains("policy") || msg.Contains("required") || msg.Contains("forbidden") || msg.Contains("language");
                    if (!hits) continue;
                    score -= severity == "high" ? 25 : 12;
                }
            }
            catch { }
        }
        return Math.Clamp(score, 0, 100);
    }

    private static JsonObject BuildFindingsSummary(IEnumerable<AiJob> jobs)
    {
        var high = 0;
        var medium = 0;
        var warning = 0;
        var byRoute = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byStage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reasons = new List<string>();
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.ResultJson)) continue;
            try
            {
                using var doc = JsonDocument.Parse(job.ResultJson);
                if (!doc.RootElement.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var finding in findings.EnumerateArray())
                {
                    var severity = finding.TryGetProperty("severity", out var sevNode) && sevNode.ValueKind == JsonValueKind.String ? (sevNode.GetString() ?? string.Empty).Trim().ToLowerInvariant() : "medium";
                    if (severity == "high") high++;
                    else if (severity == "warning") warning++;
                    else medium++;

                    var code = finding.TryGetProperty("code", out var codeNode) && codeNode.ValueKind == JsonValueKind.String ? codeNode.GetString() : null;
                    var message = finding.TryGetProperty("message", out var msgNode) && msgNode.ValueKind == JsonValueKind.String ? msgNode.GetString() : null;
                    var route = InferRepairRoute(job.StageCode ?? job.Type, code, message);
                    byRoute[route] = (byRoute.TryGetValue(route, out var currentRouteScore) ? currentRouteScore : 0) + (severity == "high" ? 3 : severity == "warning" ? 1 : 2);
                    var stageKey = job.StageCode ?? job.Type;
                    byStage[stageKey] = (byStage.TryGetValue(stageKey, out var currentStageScore) ? currentStageScore : 0) + 1;
                    if (!string.IsNullOrWhiteSpace(message) && reasons.Count < 8)
                        reasons.Add(message!);
                }
            }
            catch { }
        }

        var routeObj = new JsonObject();
        foreach (var kv in byRoute.OrderByDescending(x => x.Value)) routeObj[kv.Key] = kv.Value;
        var stageObj = new JsonObject();
        foreach (var kv in byStage.OrderByDescending(x => x.Value)) stageObj[kv.Key] = kv.Value;
        var reasonsArray = new JsonArray();
        foreach (var reason in reasons) reasonsArray.Add(reason);

        return new JsonObject
        {
            ["high"] = high,
            ["medium"] = medium,
            ["warning"] = warning,
            ["total"] = high + medium + warning,
            ["byRoute"] = routeObj,
            ["byStage"] = stageObj,
            ["reasons"] = reasonsArray,
        };
    }

    private static JsonObject BuildRepairPlanPayload(JsonObject findingsSummary, string band)
    {
        var primaryRoute = "general";
        var routes = new JsonArray();
        if (findingsSummary["byRoute"] is JsonObject byRouteObj)
        {
            foreach (var kv in byRouteObj)
            {
                if (kv.Value == null) continue;
                routes.Add(kv.Key);
                if (primaryRoute == "general")
                    primaryRoute = kv.Key;
            }
        }
        if (routes.Count == 0) routes.Add(primaryRoute);
        var suggestedAction = band switch
        {
            "ready" => "none",
            "light-repair" or "deep-repair" => "targeted-repair",
            "regenerate-from-brief" => "regenerate-from-brief",
            _ => "rewrite-brief",
        };
        return new JsonObject
        {
            ["primaryRoute"] = primaryRoute,
            ["routes"] = routes,
            ["suggestedAction"] = suggestedAction,
        };
    }

    private static FoundryRepairRoutingPlan BuildRepairRoutingPlan(IReadOnlyList<AiJob> reviews, string? scorecardJson)
    {
        var primaryRoute = "general";
        var routes = new List<string>();
        var publishRecommendation = "deep-repair";
        var overallScore = 0;
        if (!string.IsNullOrWhiteSpace(scorecardJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(scorecardJson);
                if (doc.RootElement.TryGetProperty("overallScore", out var scoreNode) && scoreNode.TryGetInt32(out var parsedScore))
                    overallScore = parsedScore;
                if (doc.RootElement.TryGetProperty("publishRecommendation", out var recNode) && recNode.ValueKind == JsonValueKind.String)
                    publishRecommendation = recNode.GetString() ?? publishRecommendation;
                if (doc.RootElement.TryGetProperty("repairPlan", out var repairPlan) && repairPlan.ValueKind == JsonValueKind.Object)
                {
                    if (repairPlan.TryGetProperty("primaryRoute", out var routeNode) && routeNode.ValueKind == JsonValueKind.String)
                        primaryRoute = routeNode.GetString() ?? primaryRoute;
                    if (repairPlan.TryGetProperty("routes", out var routesNode) && routesNode.ValueKind == JsonValueKind.Array)
                        routes = routesNode.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
            catch { }
        }
        if (routes.Count == 0) routes.Add(primaryRoute);
        var needsRepair = publishRecommendation != "ready";
        return new FoundryRepairRoutingPlan(primaryRoute, routes, publishRecommendation, overallScore, needsRepair);
    }


    private static string? ExtractBatchPublicationReadiness(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("publicationDecision", out var decision) && decision.ValueKind == JsonValueKind.Object && decision.TryGetProperty("readiness", out var readiness) && readiness.ValueKind == JsonValueKind.String)
                return readiness.GetString();
        }
        catch { }
        return null;
    }

    private static string BuildPositiveMemoryJson(AiBatch batch, IReadOnlyList<(string? Band, int? OverallScore, Dictionary<string, int> Dimensions, string? PrimaryRoute, string Status)> itemSummaries)
    {
        var orderedItems = batch.Items.OrderBy(x => x.Index).ToList();
        var strongItems = orderedItems
            .Zip(itemSummaries, (item, summary) => new { item, summary })
            .Where(x => (x.summary.OverallScore ?? 0) >= 85)
            .Take(6)
            .Select(x => new
            {
                x.item.Id,
                x.item.Index,
                x.item.TargetSkill,
                x.item.MicroGoal,
                overallScore = x.summary.OverallScore,
                band = x.summary.Band,
                strongestDimensions = x.summary.Dimensions.OrderByDescending(d => d.Value).Take(2).Select(d => d.Key).ToList(),
            })
            .ToList();

        var dimensionLeaders = itemSummaries
            .SelectMany(x => x.Dimensions)
            .GroupBy(x => x.Key)
            .Select(x => new { key = x.Key, score = (int)Math.Round(x.Average(v => v.Value), MidpointRounding.AwayFromZero) })
            .OrderByDescending(x => x.score)
            .Take(4)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            version = "wave13-memory",
            generatedAtUtc = DateTime.UtcNow,
            strongItems,
            strongDimensions = dimensionLeaders,
            notes = new[]
            {
                "Использовать сильные items как positive anchors для будущих briefs.",
                "Поддерживать ведущие dimension-сигналы при следующих batch generation волнах.",
            },
        }, JsonOptions);
    }

    private static string BuildBatchMemoryJson(AiBatch batch, IReadOnlyList<(string? Band, int? OverallScore, Dictionary<string, int> Dimensions, string? PrimaryRoute, string Status)> itemSummaries, IReadOnlyDictionary<string, int> routeCounts)
    {
        var orderedItems = batch.Items.OrderBy(x => x.Index).ToList();
        var transitions = new List<object>();
        for (var i = 1; i < orderedItems.Count; i++)
        {
            var prev = orderedItems[i - 1];
            var current = orderedItems[i];
            var difficultyDelta = current.DifficultyTarget - prev.DifficultyTarget;
            transitions.Add(new
            {
                fromIndex = prev.Index,
                toIndex = current.Index,
                fromSkill = prev.TargetSkill,
                toSkill = current.TargetSkill,
                difficultyDelta,
                risk = Math.Abs(difficultyDelta) > 1 ? "jump" : "ok",
            });
        }

        var weakItems = orderedItems
            .Zip(itemSummaries, (item, summary) => new { item.Index, item.TargetSkill, summary.OverallScore, summary.PrimaryRoute })
            .Where(x => (x.OverallScore ?? 100) < 70)
            .Take(8)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            version = "wave13-memory",
            generatedAtUtc = DateTime.UtcNow,
            routeCounts,
            weakItems,
            transitions,
            studentJourney = TryDeserializeJsonObject(batch.StudentJourneyJson),
            publicationReadiness = ExtractBatchPublicationReadiness(batch.PublicationAuditJson),
            notes = new[]
            {
                "Хранить рискованные переходы между соседними items как batch memory groundwork.",
                "Хранить типовые repair routes для последующих planner/brief решений.",
            },
        }, JsonOptions);
    }

    private static string BuildInstitutionalMemoryJson(AiBatch batch, string? plannerFeedbackJson)
    {
        var orderedItems = batch.Items.OrderBy(x => x.Index).ToList();
        var itemSignals = orderedItems
            .Select(x => new
            {
                x.Index,
                x.TargetSkill,
                x.MicroGoal,
                x.DifficultyTarget,
                overallScore = ExtractScorecardOverallScore(x.ScorecardJson),
                primaryRoute = ExtractScorecardPrimaryRoute(x.ScorecardJson),
                x.Status,
            })
            .ToList();

        var recurringRepairRoutes = itemSignals
            .Where(x => !string.IsNullOrWhiteSpace(x.primaryRoute))
            .GroupBy(x => x.primaryRoute!, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { route = x.Key, count = x.Count() })
            .OrderByDescending(x => x.count)
            .Take(5)
            .ToList();

        var strongAnchors = itemSignals
            .Where(x => (x.overallScore ?? 0) >= 85)
            .Take(6)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            version = "wave14-institutional-memory",
            generatedAtUtc = DateTime.UtcNow,
            publicationReadiness = ExtractBatchPublicationReadiness(batch.PublicationAuditJson),
            decisionSummary = TryDeserializeJsonObject(ExtractJsonPropertyOrOriginal(plannerFeedbackJson, "decisionSummary", batch.DecisionSummaryJson)),
            recurringRepairRoutes,
            strongAnchors,
            positiveMemory = TryDeserializeJsonObject(batch.PositiveMemoryJson),
            batchMemory = TryDeserializeJsonObject(batch.BatchMemoryJson),
            historicalPlannerPriors = TryDeserializeJsonObject(batch.HistoricalPlannerPriorsJson),
            plannerFeedback = TryDeserializeJsonObject(plannerFeedbackJson),
            notes = new[]
            {
                "Использовать повторяющиеся repair routes как institutional memory для следующих batch waves.",
                "Сохранять сильные anchors и стабильные skill transitions как positive planner priors.",
            },
        }, JsonOptions);
    }

    private static string BuildAntiPatternMemoryJson(AiBatch batch, string? plannerFeedbackJson)
    {
        var weakRoutes = batch.Items
            .OrderBy(x => x.Index)
            .Select(x => new { x.Index, x.TargetSkill, score = ExtractScorecardOverallScore(x.ScorecardJson), route = ExtractScorecardPrimaryRoute(x.ScorecardJson) })
            .Where(x => (x.score ?? 100) < 80 || !string.IsNullOrWhiteSpace(x.route))
            .Take(10)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            version = "wave13-anti-pattern-memory",
            generatedAtUtc = DateTime.UtcNow,
            seedNegativeMemory = TryDeserializeJsonObject(batch.NegativeMemoryJson),
            publicationFindings = ExtractFindingDescriptors(batch.PublicationAuditJson),
            batchReviewFindings = ExtractFindingDescriptors(batch.BatchReviewJson),
            studentJourneyFindings = ExtractFindingDescriptors(batch.StudentJourneyJson),
            riskyTransitions = ExtractRiskyTransitions(batch.StudentJourneyJson),
            weakRoutes,
            plannerAntiPatterns = TryDeserializeJsonObject(ExtractJsonPropertyOrOriginal(plannerFeedbackJson, "antiPatterns", null)),
            notes = new[]
            {
                "Хранить плохие transitions, слабые routes и publication blockers как anti-pattern memory.",
                "Подмешивать anti-pattern memory в future brief/review/reference pack stages.",
            },
        }, JsonOptions);
    }

    private static int? ExtractScorecardOverallScore(string? scorecardJson)
    {
        if (string.IsNullOrWhiteSpace(scorecardJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(scorecardJson);
            if (doc.RootElement.TryGetProperty("overallScore", out var node) && node.TryGetInt32(out var score))
                return score;
        }
        catch { }
        return null;
    }

    private static string? ExtractScorecardPrimaryRoute(string? scorecardJson)
    {
        if (string.IsNullOrWhiteSpace(scorecardJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(scorecardJson);
            if (doc.RootElement.TryGetProperty("repairPlan", out var repairPlan) && repairPlan.ValueKind == JsonValueKind.Object && repairPlan.TryGetProperty("primaryRoute", out var node) && node.ValueKind == JsonValueKind.String)
                return node.GetString();
        }
        catch { }
        return null;
    }

    private static IReadOnlyList<object> ExtractFindingDescriptors(string? json, int take = 8)
    {
        var result = new List<object>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var finding in findings.EnumerateArray().Take(take))
            {
                result.Add(new
                {
                    severity = finding.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.String ? sev.GetString() : null,
                    code = finding.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null,
                    message = finding.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() : null,
                    suggestedRepair = finding.TryGetProperty("suggestedRepair", out var rep) && rep.ValueKind == JsonValueKind.String ? rep.GetString() : null,
                });
            }
        }
        catch { }
        return result;
    }

    private static IReadOnlyList<object> ExtractRiskyTransitions(string? json, int take = 8)
    {
        var result = new List<object>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("transitions", out var transitions) || transitions.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var transition in transitions.EnumerateArray())
            {
                var risk = transition.TryGetProperty("risk", out var riskNode) && riskNode.ValueKind == JsonValueKind.String ? riskNode.GetString() : null;
                if (string.Equals(risk, "ok", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(new
                {
                    fromIndex = transition.TryGetProperty("fromIndex", out var fi) && fi.TryGetInt32(out var fromIndex) ? fromIndex : (int?)null,
                    toIndex = transition.TryGetProperty("toIndex", out var ti) && ti.TryGetInt32(out var toIndex) ? toIndex : (int?)null,
                    risk,
                    reasons = transition.TryGetProperty("reasons", out var reasonsNode) ? JsonSerializer.Deserialize<object>(reasonsNode.GetRawText(), JsonOptions) : null,
                });
                if (result.Count >= take) break;
            }
        }
        catch { }
        return result;
    }

    private static string InferRepairRoute(string? stageCode, string? code, string? message)
    {
        var text = $"{stageCode} {code} {message}".ToLowerInvariant();
        if (text.Contains("similar") || text.Contains("duplicate") || text.Contains("overlap") || text.Contains("batch_context")) return "brief";
        if (text.Contains("mutation") || text.Contains("hidden") || text.Contains("public") || text.Contains("edge") || text.Contains("test")) return "tests";
        if (text.Contains("runtime") || text.Contains("solution") || text.Contains("reference") || text.Contains("stderr") || text.Contains("stdout")) return "solution";
        if (text.Contains("policy") || text.Contains("forbidden") || text.Contains("required") || text.Contains("language")) return "policy";
        if (text.Contains("pedagogy") || text.Contains("style") || text.Contains("description") || text.Contains("clarity") || text.Contains("io")) return "description";
        return "general";
    }

    private static string? ExtractStringProperty(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static double? ExtractDoubleProperty(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var node) && node.TryGetDouble(out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string MergeDraftMetaJson(string? existingDraftJson, string metaKey, string? metaJson)
    {
        JsonNode rootNode;
        try
        {
            rootNode = string.IsNullOrWhiteSpace(existingDraftJson)
                ? new JsonObject()
                : JsonNode.Parse(existingDraftJson!) ?? new JsonObject();
        }
        catch
        {
            rootNode = new JsonObject();
        }

        if (rootNode is not JsonObject rootObj)
            rootObj = new JsonObject();

        // Detach "meta" from rootObj before reparenting to avoid
        // "The node already has a parent" when meta is already a child.
        var meta = rootObj["meta"] as JsonObject;
        if (meta != null)
            rootObj.Remove("meta");
        else
            meta = new JsonObject();

        meta[metaKey] = string.IsNullOrWhiteSpace(metaJson) ? null : JsonNode.Parse(metaJson!);
        rootObj["meta"] = meta;
        return rootObj.ToJsonString(JsonOptions);
    }

    private async Task MarkDraftAndBatchItemRepairedAsync(AiJob repairJob, CancellationToken ct)
    {
        ConsoleFoundry("mark-draft-repaired-start", repairJob);
        var draftId = repairJob.TargetEntityId;
        if (draftId == null && !string.IsNullOrWhiteSpace(repairJob.InputJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(repairJob.InputJson);
                draftId = ExtractGuid(doc.RootElement, "draftId");
            }
            catch { }
        }
        if (draftId == null) return;
        var draft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.Id == draftId.Value, ct);
        if (draft == null) return;
        draft.Status = "repaired";
        draft.UpdatedAtUtc = DateTime.UtcNow;
        if (draft.BatchItemId != null)
        {
            var item = await _db.AiBatchItems.FirstOrDefaultAsync(x => x.Id == draft.BatchItemId.Value, ct);
            if (item != null)
            {
                item.Status = "repaired";
                item.UpdatedAtUtc = DateTime.UtcNow;
            }
        }
        await _db.SaveChangesAsync(ct);
        if (draft.BatchId != null)
            await RefreshBatchSummaryAsync(draft.BatchId.Value, ct);
    }


    private async Task<string> BuildDecisionLogDigestJsonAsync(Guid batchId, CancellationToken ct)
    {
        var logs = await _db.AiDecisionLogs.AsNoTracking()
            .Where(x => x.BatchId == batchId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(20)
            .Select(x => new { x.StageCode, x.DecisionType, x.Message, x.CreatedAtUtc, x.BatchItemId })
            .ToListAsync(ct);

        return JsonSerializer.Serialize(new
        {
            generatedAtUtc = DateTime.UtcNow,
            total = logs.Count,
            recent = logs
        }, JsonOptions);
    }

    private static string BuildFeedbackLoopStateJson(AiBatch batch, IDictionary<string, int> routeCounts, string? readiness)
    {
        return JsonSerializer.Serialize(new
        {
            batchId = batch.Id,
            generatedAtUtc = DateTime.UtcNow,
            readiness,
            routeCounts,
            currentStage = batch.CurrentStage,
            status = batch.Status
        }, JsonOptions);
    }

    private sealed record FoundryRepairRoutingPlan(string PrimaryRoute, IReadOnlyList<string> Routes, string PublishRecommendation, int OverallScore, bool NeedsRepair)
    {
        public object ToPayload() => new
        {
            primaryRoute = PrimaryRoute,
            routes = Routes,
            publishRecommendation = PublishRecommendation,
            overallScore = OverallScore,
            needsRepair = NeedsRepair,
        };
    }

}
