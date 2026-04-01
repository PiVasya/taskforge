using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using taskforge.Data;
using taskforge.Data.Models.DTO.TaskMaths;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Assignments
{
    /// <summary>
    /// Сервис математических заданий (TaskAssignment.Type == "math").
    /// Отдельные таблицы: настройки, блоки, попытки.
    /// </summary>
    public sealed class TaskMathService : ITaskMathService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        private readonly ApplicationDbContext _db;

        public TaskMathService(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<TaskMathStartResponseDto> StartAsync(Guid assignmentId, Guid userId, CancellationToken ct)
        {
            var assignment = await _db.TaskAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
            if (assignment == null) throw new KeyNotFoundException("Assignment not found");
            if (!string.Equals(assignment.Type, "math", StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Задание не является математическим");

            var settings = await _db.TaskMathSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.TaskAssignmentId == assignmentId, ct)
                ?? new TaskMathSettings
                {
                    TaskAssignmentId = assignmentId,
                    MaxAttempts = 1,
                    PassPercent = 60,
                    ShuffleBlocks = false,
                    AllowReview = true,
                    AttemptTimeLimitsJson = "[]"
                };

            var blocks = await _db.TaskMathBlocks
                .AsNoTracking()
                .Where(x => x.TaskAssignmentId == assignmentId)
                .OrderBy(x => x.Order)
                .ThenBy(x => x.Id)
                .ToListAsync(ct);

            if (blocks.Count == 0)
                throw new ValidationException("В математическом задании пока нет блоков");

            var activeAttempt = await _db.UserTaskMathAttempts
                .Where(x => x.TaskAssignmentId == assignmentId && x.UserId == userId && x.SubmittedAt == null)
                .OrderByDescending(x => x.AttemptNumber)
                .FirstOrDefaultAsync(ct);

            if (activeAttempt != null)
            {
                var byId = blocks.ToDictionary(x => x.Id, x => x);
                var orderIds = ParseGuidArray(activeAttempt.BlockOrderJson);
                if (orderIds.Count == 0)
                    orderIds = blocks.Select(x => x.Id).ToList();

                orderIds = orderIds.Where(byId.ContainsKey).ToList();
                var missing = byId.Keys.Where(id => !orderIds.Contains(id)).ToList();
                if (missing.Count > 0)
                {
                    if (settings.ShuffleBlocks)
                        Shuffle(missing, new Random(SeedFromGuid(activeAttempt.Id)));

                    orderIds.AddRange(missing);
                    activeAttempt.BlockOrderJson = JsonSerializer.Serialize(orderIds, JsonOptions);
                    activeAttempt.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }

                var orderedBlocks = orderIds
                    .Select(id => byId.TryGetValue(id, out var b) ? b : null)
                    .Where(x => x != null)
                    .Cast<TaskMathBlock>()
                    .ToList();

                if (orderedBlocks.Count == 0)
                    orderedBlocks = blocks;

                return new TaskMathStartResponseDto
                {
                    AttemptId = activeAttempt.Id,
                    AttemptNumber = activeAttempt.AttemptNumber,
                    MaxAttempts = settings.MaxAttempts <= 0 ? int.MaxValue : settings.MaxAttempts,
                    PassPercent = ClampPercent(settings.PassPercent),
                    TimeLimitSeconds = activeAttempt.TimeLimitSeconds,
                    StartedAt = activeAttempt.StartedAt,
                    ShuffleBlocks = settings.ShuffleBlocks,
                    Blocks = BuildPublicBlocks(activeAttempt.Id, orderedBlocks)
                };
            }

            var usedAttempts = await _db.UserTaskMathAttempts
                .AsNoTracking()
                .CountAsync(x => x.TaskAssignmentId == assignmentId && x.UserId == userId, ct);

            var attemptNumber = usedAttempts + 1;
            var maxAttempts = settings.MaxAttempts <= 0 ? int.MaxValue : settings.MaxAttempts;
            if (attemptNumber > maxAttempts)
                throw new ValidationException("Достигнут лимит попыток");

            var attemptId = Guid.NewGuid();
            var timeLimits = ParseTimeLimits(settings.AttemptTimeLimitsJson);
            int? timeLimitSeconds = null;
            if (attemptNumber >= 1 && attemptNumber <= timeLimits.Count)
            {
                var v = timeLimits[attemptNumber - 1];
                if (v.HasValue && v.Value > 0) timeLimitSeconds = v.Value;
            }

            var blockOrder = blocks.Select(x => x.Id).ToList();
            if (settings.ShuffleBlocks)
                Shuffle(blockOrder, new Random(SeedFromGuid(attemptId)));

            var attempt = new UserTaskMathAttempt
            {
                Id = attemptId,
                TaskAssignmentId = assignmentId,
                UserId = userId,
                AttemptNumber = attemptNumber,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow,
                TimeLimitSeconds = timeLimitSeconds,
                BlockOrderJson = JsonSerializer.Serialize(blockOrder, JsonOptions)
            };

            _db.UserTaskMathAttempts.Add(attempt);
            await _db.SaveChangesAsync(ct);

            var map = blocks.ToDictionary(x => x.Id, x => x);
            var ordered = blockOrder.Where(map.ContainsKey).Select(id => map[id]).ToList();

            return new TaskMathStartResponseDto
            {
                AttemptId = attempt.Id,
                AttemptNumber = attempt.AttemptNumber,
                MaxAttempts = maxAttempts,
                PassPercent = ClampPercent(settings.PassPercent),
                TimeLimitSeconds = timeLimitSeconds,
                StartedAt = attempt.StartedAt,
                ShuffleBlocks = settings.ShuffleBlocks,
                Blocks = BuildPublicBlocks(attemptId, ordered)
            };
        }

        public async Task<TaskMathSubmitResultDto> SubmitAsync(Guid assignmentId, Guid userId, TaskMathSubmitRequestDto request, CancellationToken ct)
        {
            if (request.AttemptId == Guid.Empty)
                throw new ValidationException("AttemptId обязателен");

            var attempt = await _db.UserTaskMathAttempts
                .FirstOrDefaultAsync(x => x.Id == request.AttemptId && x.TaskAssignmentId == assignmentId && x.UserId == userId, ct);
            if (attempt == null) throw new KeyNotFoundException("Attempt not found");
            if (attempt.SubmittedAt != null) throw new ValidationException("Попытка уже отправлена");

            var settings = await _db.TaskMathSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TaskAssignmentId == assignmentId, ct)
                ?? new TaskMathSettings { TaskAssignmentId = assignmentId, AllowReview = true };

            var blocks = await _db.TaskMathBlocks.AsNoTracking()
                .Where(x => x.TaskAssignmentId == assignmentId)
                .ToListAsync(ct);
            if (blocks.Count == 0) throw new ValidationException("В задании нет блоков");

            var blockMap = blocks.ToDictionary(x => x.Id, x => x);
            var orderIds = SafeDeserialize<List<Guid>>(attempt.BlockOrderJson) ?? new List<Guid>();
            if (orderIds.Count == 0)
                orderIds = blocks.OrderBy(x => x.Order).Select(x => x.Id).ToList();

            var answerMap = (request.Answers ?? new List<TaskMathAnswerDto>())
                .GroupBy(x => x.BlockId)
                .ToDictionary(g => g.Key, g => g.First());

            var totalScore = 0;
            var earnedScore = 0;

            foreach (var id in orderIds)
            {
                if (!blockMap.TryGetValue(id, out var block)) continue;
                if (IsInfo(block.Kind)) continue;

                var score = Math.Max(0, block.Score);
                totalScore += score;

                answerMap.TryGetValue(id, out var ans);
                if (IsCorrect(block, ans))
                    earnedScore += score;
            }

            var scorePercent = totalScore <= 0 ? 100 : (int)Math.Floor(earnedScore * 100.0 / totalScore);

            var timeExpired = false;
            if (attempt.TimeLimitSeconds.HasValue && attempt.TimeLimitSeconds.Value > 0)
            {
                var elapsed = (DateTime.UtcNow - attempt.StartedAt).TotalSeconds;
                timeExpired = elapsed > attempt.TimeLimitSeconds.Value;
            }

            attempt.AnswersJson = JsonSerializer.Serialize(request, JsonOptions);
            attempt.TotalScore = totalScore;
            attempt.EarnedScore = earnedScore;
            attempt.ScorePercent = scorePercent;
            attempt.TimeExpired = timeExpired;
            attempt.Passed = !timeExpired && scorePercent >= ClampPercent(settings.PassPercent);
            attempt.SubmittedAt = DateTime.UtcNow;
            attempt.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            return new TaskMathSubmitResultDto
            {
                AttemptId = attempt.Id,
                AttemptNumber = attempt.AttemptNumber,
                MaxAttempts = settings.MaxAttempts <= 0 ? int.MaxValue : settings.MaxAttempts,
                PassPercent = ClampPercent(settings.PassPercent),
                TotalScore = totalScore,
                EarnedScore = earnedScore,
                ScorePercent = scorePercent,
                TimeExpired = timeExpired,
                Passed = attempt.Passed
            };
        }

        public async Task<List<TaskMathAttemptListItemDto>> GetAttemptsAsync(
            Guid userId,
            Guid? courseId,
            Guid? assignmentId,
            int? days,
            int skip,
            int take,
            CancellationToken ct)
        {
            if (skip < 0) skip = 0;
            if (take <= 0) take = 50;
            if (take > 5000) take = 5000;

            var q = _db.UserTaskMathAttempts
                .AsNoTracking()
                .Include(a => a.TaskAssignment!)
                .ThenInclude(t => t.Course)
                .Where(a => a.UserId == userId && a.SubmittedAt != null);

            if (courseId.HasValue)
                q = q.Where(a => a.TaskAssignment != null && a.TaskAssignment.CourseId == courseId.Value);
            if (assignmentId.HasValue)
                q = q.Where(a => a.TaskAssignmentId == assignmentId.Value);
            if (days.HasValue)
            {
                var since = DateTime.UtcNow.AddDays(-Math.Abs(days.Value));
                q = q.Where(a => a.SubmittedAt >= since);
            }

            var list = await q
                .OrderByDescending(a => a.SubmittedAt)
                .ThenByDescending(a => a.AttemptNumber)
                .Skip(skip)
                .Take(take)
                .ToListAsync(ct);

            if (list.Count == 0) return new List<TaskMathAttemptListItemDto>();

            var ids = list.Select(x => x.TaskAssignmentId).Distinct().ToArray();
            var settings = await _db.TaskMathSettings
                .AsNoTracking()
                .Where(s => ids.Contains(s.TaskAssignmentId))
                .ToListAsync(ct);
            var allowMap = settings.ToDictionary(s => s.TaskAssignmentId, s => s.AllowReview);

            return list.Select(a =>
            {
                var ta = a.TaskAssignment;
                var c = ta?.Course;
                return new TaskMathAttemptListItemDto
                {
                    AttemptId = a.Id,
                    TaskAssignmentId = a.TaskAssignmentId,
                    CourseId = ta?.CourseId ?? Guid.Empty,
                    CourseTitle = c?.Title ?? string.Empty,
                    AssignmentTitle = ta?.Title ?? string.Empty,
                    AttemptNumber = a.AttemptNumber,
                    SubmittedAt = a.SubmittedAt ?? a.UpdatedAt,
                    TotalScore = a.TotalScore,
                    EarnedScore = a.EarnedScore,
                    ScorePercent = a.ScorePercent,
                    Passed = a.Passed,
                    TimeExpired = a.TimeExpired,
                    AllowReview = allowMap.TryGetValue(a.TaskAssignmentId, out var ar) ? ar : true,
                };
            }).ToList();
        }

        public async Task<TaskMathAttemptReviewDto?> GetAttemptReviewAsync(Guid userId, Guid attemptId, bool isAdmin, CancellationToken ct)
        {
            var attempt = await _db.UserTaskMathAttempts
                .AsNoTracking()
                .Include(a => a.User)
                .Include(a => a.TaskAssignment!)
                .ThenInclude(t => t.Course)
                .FirstOrDefaultAsync(a => a.Id == attemptId, ct);

            if (attempt == null) return null;
            if (!isAdmin && attempt.UserId != userId) return null;
            if (attempt.SubmittedAt == null) return null;

            var settings = await _db.TaskMathSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TaskAssignmentId == attempt.TaskAssignmentId, ct)
                ?? new TaskMathSettings { TaskAssignmentId = attempt.TaskAssignmentId, AllowReview = true };

            if (!isAdmin && !settings.AllowReview)
                throw new UnauthorizedAccessException("Просмотр попытки отключён");

            var blocks = await _db.TaskMathBlocks
                .AsNoTracking()
                .Where(x => x.TaskAssignmentId == attempt.TaskAssignmentId)
                .OrderBy(x => x.Order)
                .ThenBy(x => x.Id)
                .ToListAsync(ct);

            var blockMap = blocks.ToDictionary(x => x.Id, x => x);
            var order = ParseGuidArray(attempt.BlockOrderJson);
            if (order.Count == 0)
                order = blocks.Select(x => x.Id).ToList();

            var req = SafeDeserialize<TaskMathSubmitRequestDto>(attempt.AnswersJson) ?? new TaskMathSubmitRequestDto
            {
                AttemptId = attempt.Id,
                Answers = new List<TaskMathAnswerDto>()
            };
            var ansMap = (req.Answers ?? new List<TaskMathAnswerDto>())
                .Where(x => x != null && x.BlockId != Guid.Empty)
                .GroupBy(x => x.BlockId)
                .ToDictionary(g => g.Key, g => g.Last());

            var dto = new TaskMathAttemptReviewDto
            {
                AttemptId = attempt.Id,
                TaskAssignmentId = attempt.TaskAssignmentId,
                CourseId = attempt.TaskAssignment?.CourseId ?? Guid.Empty,
                CourseTitle = attempt.TaskAssignment?.Course?.Title ?? string.Empty,
                AssignmentTitle = attempt.TaskAssignment?.Title ?? string.Empty,
                UserId = attempt.UserId,
                UserEmail = attempt.User?.Email,
                AttemptNumber = attempt.AttemptNumber,
                StartedAt = attempt.StartedAt,
                SubmittedAt = attempt.SubmittedAt.Value,
                PassPercent = ClampPercent(settings.PassPercent),
                TotalScore = attempt.TotalScore,
                EarnedScore = attempt.EarnedScore,
                ScorePercent = attempt.ScorePercent,
                Passed = attempt.Passed,
                TimeExpired = attempt.TimeExpired,
                AllowReview = settings.AllowReview,
                Blocks = new List<TaskMathAttemptReviewBlockDto>()
            };

            var idx = 0;
            foreach (var blockId in order)
            {
                if (!blockMap.TryGetValue(blockId, out var block)) continue;
                ansMap.TryGetValue(blockId, out var ans);
                dto.Blocks.Add(BuildReviewBlockDto(block, ans, idx++));
            }

            return dto;
        }

        public async Task DeleteAttemptAsync(Guid attemptId, CancellationToken ct)
        {
            var entity = await _db.UserTaskMathAttempts.FirstOrDefaultAsync(x => x.Id == attemptId, ct);
            if (entity == null) return;
            _db.UserTaskMathAttempts.Remove(entity);
            await _db.SaveChangesAsync(ct);
        }

        public async Task<TaskMathEditDto> GetEditAsync(Guid assignmentId, Guid userId, CancellationToken ct)
        {
            var assignment = await _db.TaskAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct)
                ?? throw new KeyNotFoundException("Assignment not found");
            if (!string.Equals(assignment.Type, "math", StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Задание не является math");

            var settings = await _db.TaskMathSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TaskAssignmentId == assignmentId, ct)
                ?? new TaskMathSettings { TaskAssignmentId = assignmentId, AllowReview = true };

            var blocks = await _db.TaskMathBlocks.AsNoTracking()
                .Where(x => x.TaskAssignmentId == assignmentId)
                .OrderBy(x => x.Order)
                .ThenBy(x => x.Id)
                .ToListAsync(ct);

            var dto = new TaskMathEditDto
            {
                Settings = new TaskMathSettingsDto
                {
                    MaxAttempts = settings.MaxAttempts <= 0 ? 1 : settings.MaxAttempts,
                    PassPercent = ClampPercent(settings.PassPercent),
                    ShuffleBlocks = settings.ShuffleBlocks,
                    AllowReview = settings.AllowReview,
                    AttemptTimeLimitsSeconds = ParseTimeLimits(settings.AttemptTimeLimitsJson)
                },
                Blocks = new List<TaskMathBlockEditDto>()
            };

            foreach (var block in blocks)
            {
                var item = new TaskMathBlockEditDto
                {
                    Id = block.Id,
                    Order = block.Order,
                    Kind = NormalizeKind(block.Kind),
                    Prompt = block.Prompt,
                    PromptContentJson = block.PromptContentJson,
                    Score = Math.Max(0, block.Score),
                    IsRequired = block.IsRequired,
                };

                FillEditDtoFromBlockData(item, block);
                dto.Blocks.Add(item);
            }

            return dto;
        }

        public async Task SaveEditAsync(Guid assignmentId, Guid userId, TaskMathEditDto dto, CancellationToken ct)
        {
            var assignment = await _db.TaskAssignments
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == assignmentId, ct)
                ?? throw new KeyNotFoundException("Assignment not found");

            if (!string.Equals(assignment.Type, "math", StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Задание не является math");

            var settings = await _db.TaskMathSettings.FirstOrDefaultAsync(x => x.TaskAssignmentId == assignmentId, ct);
            if (settings == null)
            {
                settings = new TaskMathSettings
                {
                    Id = Guid.NewGuid(),
                    TaskAssignmentId = assignmentId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                _db.TaskMathSettings.Add(settings);
            }

            var normalizedSettings = dto.Settings ?? new TaskMathSettingsDto();
            settings.MaxAttempts = Math.Max(1, normalizedSettings.MaxAttempts);
            settings.PassPercent = ClampPercent(normalizedSettings.PassPercent);
            settings.ShuffleBlocks = normalizedSettings.ShuffleBlocks;
            settings.AllowReview = normalizedSettings.AllowReview;
            settings.AttemptTimeLimitsJson = JsonSerializer.Serialize(
                (normalizedSettings.AttemptTimeLimitsSeconds ?? new List<int?>())
                    .Select(v => (v.HasValue && v.Value > 0) ? v : null)
                    .ToList(),
                JsonOptions);
            settings.UpdatedAt = DateTime.UtcNow;

            var existing = await _db.TaskMathBlocks.Where(x => x.TaskAssignmentId == assignmentId).ToListAsync(ct);
            var incoming = dto.Blocks ?? new List<TaskMathBlockEditDto>();
            var incomingIds = incoming.Where(x => x.Id != Guid.Empty && x.Id != default).Select(x => x.Id).ToHashSet();

            foreach (var old in existing.Where(x => !incomingIds.Contains(x.Id)).ToList())
                _db.TaskMathBlocks.Remove(old);

            for (var i = 0; i < incoming.Count; i++)
            {
                var blockDto = incoming[i] ?? new TaskMathBlockEditDto();
                NormalizeEditDto(blockDto, i);
                ValidateEditDto(blockDto, i);

                var entity = existing.FirstOrDefault(x => x.Id == blockDto.Id);
                if (entity == null)
                {
                    entity = new TaskMathBlock
                    {
                        Id = blockDto.Id == Guid.Empty ? Guid.NewGuid() : blockDto.Id,
                        TaskAssignmentId = assignmentId,
                        CreatedAt = DateTime.UtcNow,
                    };
                    _db.TaskMathBlocks.Add(entity);
                }

                entity.Order = i;
                entity.Kind = NormalizeKind(blockDto.Kind);
                entity.Prompt = (blockDto.Prompt ?? string.Empty).Trim();
                entity.PromptContentJson = string.IsNullOrWhiteSpace(blockDto.PromptContentJson) ? null : blockDto.PromptContentJson;
                entity.Score = Math.Max(0, blockDto.Score);
                entity.IsRequired = blockDto.IsRequired;
                entity.DataJson = BuildDataJson(blockDto);
                entity.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);
        }

        private static bool IsCorrect(TaskMathBlock block, TaskMathAnswerDto? ans)
        {
            var kind = NormalizeKind(block.Kind);
            if (IsInfo(kind)) return true;

            if (kind == "single-choice" || kind == "multi-choice")
            {
                var data = SafeDeserialize<ChoiceData>(block.DataJson) ?? new ChoiceData();
                var selected = (ans?.SelectedOptionKeys ?? new List<string>())
                    .Select(NormalizeToken)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var correct = (data.CorrectOptionKeys ?? new List<string>())
                    .Select(NormalizeToken)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (kind == "single-choice")
                    return selected.Count == 1 && correct.Count == 1 && selected[0] == correct[0];

                if (selected.Count != correct.Count) return false;
                return !selected.Except(correct, StringComparer.Ordinal).Any();
            }

            if (kind == "number")
            {
                var data = SafeDeserialize<TextData>(block.DataJson) ?? new TextData();
                var text = NormalizeAnswerText(ans?.Text, data.Trim, data.CaseSensitive);
                if (string.IsNullOrWhiteSpace(text)) return false;
                if (!double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var actual))
                    return false;

                var tolerance = Math.Max(0, data.NumericTolerance ?? 0d);
                foreach (var raw in data.AcceptedAnswers ?? new List<string>())
                {
                    var expectedText = NormalizeAnswerText(raw, data.Trim, data.CaseSensitive);
                    if (!double.TryParse(expectedText.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var expected))
                        continue;
                    if (Math.Abs(actual - expected) <= tolerance) return true;
                }

                return false;
            }

            if (kind == "expression")
            {
                var data = SafeDeserialize<TextData>(block.DataJson) ?? new TextData();
                var text = NormalizeExpression(ans?.Text, data.Trim, data.CaseSensitive);
                if (string.IsNullOrWhiteSpace(text)) return false;
                return (data.AcceptedAnswers ?? new List<string>())
                    .Select(x => NormalizeExpression(x, data.Trim, data.CaseSensitive))
                    .Any(x => x == text);
            }

            if (kind == "set")
            {
                var data = SafeDeserialize<TextData>(block.DataJson) ?? new TextData();
                var actual = ParseTokenSet(ans?.Text, data.Trim, data.CaseSensitive);
                var expected = ParseTokenSet(string.Join(",", data.AcceptedAnswers ?? new List<string>()), data.Trim, data.CaseSensitive);
                if (actual.Count == 0 || expected.Count == 0) return false;
                return actual.SetEquals(expected);
            }

            if (kind == "order")
            {
                var data = SafeDeserialize<OrderData>(block.DataJson) ?? new OrderData();
                var actual = (ans?.OrderedItems ?? new List<string>()).Select(NormalizeToken).ToList();
                var expected = (data.Items ?? new List<string>()).Select(NormalizeToken).ToList();
                if (actual.Count != expected.Count || actual.Count == 0) return false;
                for (var i = 0; i < actual.Count; i++)
                    if (!string.Equals(actual[i], expected[i], StringComparison.Ordinal)) return false;
                return true;
            }

            if (kind == "match")
            {
                var data = SafeDeserialize<MatchData>(block.DataJson) ?? new MatchData();
                var actualPairs = (ans?.MatchPairs ?? new List<TaskMathMatchPairDto>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.LeftKey) && !string.IsNullOrWhiteSpace(x.RightKey))
                    .Select(x => $"{NormalizeToken(x.LeftKey)}::{NormalizeToken(x.RightKey)}")
                    .Distinct(StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal);
                var expectedPairs = (data.Pairs ?? new List<TaskMathMatchPairDto>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.LeftKey) && !string.IsNullOrWhiteSpace(x.RightKey))
                    .Select(x => $"{NormalizeToken(x.LeftKey)}::{NormalizeToken(x.RightKey)}")
                    .Distinct(StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal);

                return expectedPairs.Count > 0 && actualPairs.SetEquals(expectedPairs);
            }

            return false;
        }

        private static TaskMathAttemptReviewBlockDto BuildReviewBlockDto(TaskMathBlock block, TaskMathAnswerDto? userAnswer, int order)
        {
            var kind = NormalizeKind(block.Kind);
            var dto = new TaskMathAttemptReviewBlockDto
            {
                Id = block.Id,
                Order = order,
                Kind = kind,
                Prompt = block.Prompt,
                PromptContentJson = block.PromptContentJson,
                Score = Math.Max(0, block.Score),
                IsRequired = block.IsRequired,
                UserAnswer = userAnswer,
                IsCorrect = IsInfo(kind) || IsCorrect(block, userAnswer),
            };

            if (kind == "single-choice" || kind == "multi-choice")
            {
                var data = SafeDeserialize<ChoiceData>(block.DataJson) ?? new ChoiceData();
                dto.Options = data.Options ?? new List<TaskMathOptionDto>();
                dto.CorrectOptionKeys = data.CorrectOptionKeys ?? new List<string>();
                return dto;
            }

            if (kind == "number" || kind == "expression" || kind == "set")
            {
                var data = SafeDeserialize<TextData>(block.DataJson) ?? new TextData();
                dto.AcceptedAnswers = data.AcceptedAnswers ?? new List<string>();
                dto.NumericTolerance = data.NumericTolerance;
                return dto;
            }

            if (kind == "order")
            {
                var data = SafeDeserialize<OrderData>(block.DataJson) ?? new OrderData();
                dto.OrderItems = data.Items ?? new List<string>();
                return dto;
            }

            if (kind == "match")
            {
                var data = SafeDeserialize<MatchData>(block.DataJson) ?? new MatchData();
                dto.MatchLeftItems = data.LeftItems ?? new List<TaskMathOptionDto>();
                dto.MatchRightItems = data.RightItems ?? new List<TaskMathOptionDto>();
                dto.MatchPairs = data.Pairs ?? new List<TaskMathMatchPairDto>();
                return dto;
            }

            return dto;
        }

        private static List<TaskMathBlockPublicDto> BuildPublicBlocks(Guid attemptId, List<TaskMathBlock> orderedBlocks)
        {
            var result = new List<TaskMathBlockPublicDto>(orderedBlocks.Count);
            for (var idx = 0; idx < orderedBlocks.Count; idx++)
            {
                var block = orderedBlocks[idx];
                var kind = NormalizeKind(block.Kind);
                var dto = new TaskMathBlockPublicDto
                {
                    Id = block.Id,
                    Order = idx,
                    Kind = kind,
                    Prompt = block.Prompt,
                    PromptContentJson = block.PromptContentJson,
                    Score = Math.Max(0, block.Score),
                    IsRequired = block.IsRequired,
                };

                if (kind == "single-choice" || kind == "multi-choice")
                {
                    var data = SafeDeserialize<ChoiceData>(block.DataJson) ?? new ChoiceData();
                    var opts = (data.Options ?? new List<TaskMathOptionDto>())
                        .Select(x => new TaskMathOptionDto { Key = x.Key, Text = x.Text })
                        .ToList();
                    var rnd = new Random(SeedFromGuid(attemptId) ^ SeedFromGuid(block.Id));
                    Shuffle(opts, rnd);
                    dto.Options = opts;
                }
                else if (kind == "order")
                {
                    var data = SafeDeserialize<OrderData>(block.DataJson) ?? new OrderData();
                    var items = (data.Items ?? new List<string>()).ToList();
                    var rnd = new Random(SeedFromGuid(attemptId) ^ SeedFromGuid(block.Id));
                    Shuffle(items, rnd);
                    dto.OrderItems = items;
                }
                else if (kind == "match")
                {
                    var data = SafeDeserialize<MatchData>(block.DataJson) ?? new MatchData();
                    dto.MatchLeftItems = data.LeftItems ?? new List<TaskMathOptionDto>();
                    var right = (data.RightItems ?? new List<TaskMathOptionDto>()).Select(x => new TaskMathOptionDto { Key = x.Key, Text = x.Text }).ToList();
                    var rnd = new Random(SeedFromGuid(attemptId) ^ SeedFromGuid(block.Id));
                    Shuffle(right, rnd);
                    dto.MatchRightItems = right;
                }

                result.Add(dto);
            }

            return result;
        }

        private static void FillEditDtoFromBlockData(TaskMathBlockEditDto dto, TaskMathBlock block)
        {
            var kind = NormalizeKind(block.Kind);
            if (kind == "single-choice" || kind == "multi-choice")
            {
                var data = SafeDeserialize<ChoiceData>(block.DataJson) ?? new ChoiceData();
                dto.Options = data.Options ?? new List<TaskMathOptionDto>();
                dto.CorrectOptionKeys = data.CorrectOptionKeys ?? new List<string>();
                return;
            }

            if (kind == "number" || kind == "expression" || kind == "set")
            {
                var data = SafeDeserialize<TextData>(block.DataJson) ?? new TextData();
                dto.AcceptedAnswers = data.AcceptedAnswers ?? new List<string>();
                dto.CaseSensitive = data.CaseSensitive;
                dto.Trim = data.Trim;
                dto.NumericTolerance = data.NumericTolerance;
                return;
            }

            if (kind == "order")
            {
                var data = SafeDeserialize<OrderData>(block.DataJson) ?? new OrderData();
                dto.OrderItems = data.Items ?? new List<string>();
                return;
            }

            if (kind == "match")
            {
                var data = SafeDeserialize<MatchData>(block.DataJson) ?? new MatchData();
                dto.MatchLeftItems = data.LeftItems ?? new List<TaskMathOptionDto>();
                dto.MatchRightItems = data.RightItems ?? new List<TaskMathOptionDto>();
                dto.MatchPairs = data.Pairs ?? new List<TaskMathMatchPairDto>();
            }
        }

        private static string BuildDataJson(TaskMathBlockEditDto dto)
        {
            var kind = NormalizeKind(dto.Kind);
            object payload = new { };

            if (kind == "single-choice" || kind == "multi-choice")
            {
                payload = new ChoiceData
                {
                    Options = (dto.Options ?? new List<TaskMathOptionDto>())
                        .Where(x => !string.IsNullOrWhiteSpace(x?.Key) && !string.IsNullOrWhiteSpace(x?.Text))
                        .Select(x => new TaskMathOptionDto { Key = x.Key.Trim(), Text = x.Text.Trim() })
                        .ToList(),
                    CorrectOptionKeys = (dto.CorrectOptionKeys ?? new List<string>())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                };
            }
            else if (kind == "number" || kind == "expression" || kind == "set")
            {
                payload = new TextData
                {
                    AcceptedAnswers = (dto.AcceptedAnswers ?? new List<string>())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim())
                        .ToList(),
                    CaseSensitive = dto.CaseSensitive ?? false,
                    Trim = dto.Trim ?? true,
                    NumericTolerance = kind == "number" ? (dto.NumericTolerance ?? 0d) : null,
                };
            }
            else if (kind == "order")
            {
                payload = new OrderData
                {
                    Items = (dto.OrderItems ?? new List<string>())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim())
                        .ToList(),
                };
            }
            else if (kind == "match")
            {
                payload = new MatchData
                {
                    LeftItems = (dto.MatchLeftItems ?? new List<TaskMathOptionDto>())
                        .Where(x => !string.IsNullOrWhiteSpace(x?.Key) && !string.IsNullOrWhiteSpace(x?.Text))
                        .Select(x => new TaskMathOptionDto { Key = x.Key.Trim(), Text = x.Text.Trim() })
                        .ToList(),
                    RightItems = (dto.MatchRightItems ?? new List<TaskMathOptionDto>())
                        .Where(x => !string.IsNullOrWhiteSpace(x?.Key) && !string.IsNullOrWhiteSpace(x?.Text))
                        .Select(x => new TaskMathOptionDto { Key = x.Key.Trim(), Text = x.Text.Trim() })
                        .ToList(),
                    Pairs = (dto.MatchPairs ?? new List<TaskMathMatchPairDto>())
                        .Where(x => !string.IsNullOrWhiteSpace(x?.LeftKey) && !string.IsNullOrWhiteSpace(x?.RightKey))
                        .Select(x => new TaskMathMatchPairDto { LeftKey = x.LeftKey.Trim(), RightKey = x.RightKey.Trim() })
                        .ToList(),
                };
            }

            return JsonSerializer.Serialize(payload, JsonOptions);
        }

        private static void NormalizeEditDto(TaskMathBlockEditDto dto, int idx)
        {
            dto.Kind = NormalizeKind(dto.Kind);
            dto.Prompt = string.IsNullOrWhiteSpace(dto.Prompt) ? $"Блок #{idx + 1}" : dto.Prompt.Trim();
            dto.Score = Math.Max(0, dto.Score);
        }

        private static void ValidateEditDto(TaskMathBlockEditDto dto, int idx)
        {
            var prefix = $"Блок #{idx + 1}";
            var kind = NormalizeKind(dto.Kind);

            if (string.IsNullOrWhiteSpace(dto.Prompt) && string.IsNullOrWhiteSpace(dto.PromptContentJson))
                throw new ValidationException($"{prefix}: заполни текст блока или rich-условие.");

            if (kind == "single-choice" || kind == "multi-choice")
            {
                var opts = (dto.Options ?? new List<TaskMathOptionDto>())
                    .Where(x => !string.IsNullOrWhiteSpace(x?.Text))
                    .ToList();
                var correct = (dto.CorrectOptionKeys ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (opts.Count < 2) throw new ValidationException($"{prefix}: нужно минимум 2 варианта ответа.");
                if (correct.Count == 0) throw new ValidationException($"{prefix}: отметь хотя бы один правильный вариант.");
                if (kind == "single-choice" && correct.Count != 1) throw new ValidationException($"{prefix}: single-choice должен иметь ровно 1 правильный вариант.");
            }

            if (kind == "number" || kind == "expression" || kind == "set")
            {
                var answers = (dto.AcceptedAnswers ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (answers.Count == 0) throw new ValidationException($"{prefix}: добавь хотя бы один допустимый ответ.");
                if (kind == "number" && (dto.NumericTolerance ?? 0d) < 0) throw new ValidationException($"{prefix}: tolerance не может быть отрицательным.");
            }

            if (kind == "order")
            {
                var items = (dto.OrderItems ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (items.Count < 2) throw new ValidationException($"{prefix}: для блока порядка нужно минимум 2 шага.");
            }

            if (kind == "match")
            {
                var left = (dto.MatchLeftItems ?? new List<TaskMathOptionDto>()).Where(x => !string.IsNullOrWhiteSpace(x?.Key) && !string.IsNullOrWhiteSpace(x?.Text)).ToList();
                var right = (dto.MatchRightItems ?? new List<TaskMathOptionDto>()).Where(x => !string.IsNullOrWhiteSpace(x?.Key) && !string.IsNullOrWhiteSpace(x?.Text)).ToList();
                var pairs = (dto.MatchPairs ?? new List<TaskMathMatchPairDto>()).Where(x => !string.IsNullOrWhiteSpace(x?.LeftKey) && !string.IsNullOrWhiteSpace(x?.RightKey)).ToList();
                if (left.Count == 0 || right.Count == 0 || pairs.Count == 0) throw new ValidationException($"{prefix}: заполни левую/правую колонку и пары.");
            }
        }

        private static string NormalizeKind(string? kind)
        {
            var k = (kind ?? "").Trim().ToLowerInvariant();
            return k switch
            {
                "info" or "statement" or "text-block" => "info",
                "number" or "numeric" => "number",
                "expression" or "formula" => "expression",
                "set" or "set-input" => "set",
                "single" or "single-choice" or "choice" => "single-choice",
                "multi" or "multiple" or "multi-choice" => "multi-choice",
                "order" or "sequence" => "order",
                "match" or "pairs" => "match",
                _ => "info"
            };
        }

        private static bool IsInfo(string? kind) => NormalizeKind(kind) == "info";

        private static string NormalizeToken(string? value) => (value ?? string.Empty).Trim();

        private static string NormalizeAnswerText(string? text, bool trim, bool caseSensitive)
        {
            var value = text ?? string.Empty;
            if (trim) value = value.Trim();
            if (!caseSensitive) value = value.ToLowerInvariant();
            return value;
        }

        private static string NormalizeExpression(string? text, bool trim, bool caseSensitive)
        {
            var value = NormalizeAnswerText(text, trim, caseSensitive);
            value = value.Replace(" ", string.Empty).Replace("\t", string.Empty).Replace("\n", string.Empty).Replace("\r", string.Empty);
            value = value.Replace("−", "-").Replace("·", "*");
            return value;
        }

        private static HashSet<string> ParseTokenSet(string? text, bool trim, bool caseSensitive)
        {
            var source = text ?? string.Empty;
            source = source.Replace(";", ",").Replace("|", ",").Replace("\n", ",");
            return source
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => NormalizeAnswerText(x, trim, caseSensitive))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static List<int?> ParseTimeLimits(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<int?>();
            try
            {
                return JsonSerializer.Deserialize<List<int?>>(json, JsonOptions) ?? new List<int?>();
            }
            catch
            {
                return new List<int?>();
            }
        }

        private static List<Guid> ParseGuidArray(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<Guid>();
            try
            {
                return JsonSerializer.Deserialize<List<Guid>>(json, JsonOptions) ?? new List<Guid>();
            }
            catch
            {
                return new List<Guid>();
            }
        }

        private static T? SafeDeserialize<T>(string? json) where T : class
        {
            try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, JsonOptions); }
            catch { return null; }
        }

        private static int ClampPercent(int value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }

        private static void Shuffle<T>(IList<T> list, Random rnd)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = rnd.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static int SeedFromGuid(Guid id)
        {
            var b = id.ToByteArray();
            unchecked
            {
                var s = 17;
                for (var i = 0; i < b.Length; i++) s = (s * 31) + b[i];
                return s;
            }
        }

        private sealed class ChoiceData
        {
            public List<TaskMathOptionDto> Options { get; set; } = new();
            public List<string> CorrectOptionKeys { get; set; } = new();
        }

        private sealed class TextData
        {
            public List<string> AcceptedAnswers { get; set; } = new();
            public bool CaseSensitive { get; set; }
            public bool Trim { get; set; } = true;
            public double? NumericTolerance { get; set; }
        }

        private sealed class OrderData
        {
            public List<string> Items { get; set; } = new();
        }

        private sealed class MatchData
        {
            public List<TaskMathOptionDto> LeftItems { get; set; } = new();
            public List<TaskMathOptionDto> RightItems { get; set; } = new();
            public List<TaskMathMatchPairDto> Pairs { get; set; } = new();
        }
    }
}
