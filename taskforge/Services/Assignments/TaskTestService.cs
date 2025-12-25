using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using taskforge.Data;
using taskforge.Data.Models.DTO.TaskTests;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Assignments
{
    /// <summary>
    /// Сервис тестовых заданий (TaskAssignment.Type == "test").
    /// </summary>
    public sealed class TaskTestService : ITaskTestService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        private readonly ApplicationDbContext _db;

        public TaskTestService(ApplicationDbContext db)
        {
            _db = db;
        }

        // ===== Solve =====
        public async Task<TaskTestStartResponseDto> StartAsync(Guid assignmentId, Guid userId, CancellationToken ct)
        {
            var assignment = await _db.TaskAssignments
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
            if (assignment == null) throw new KeyNotFoundException("Assignment not found");
            if (!string.Equals(assignment.Type, "test", StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Задание не является тестом");

            var settings = await _db.TaskTestSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TaskAssignmentId == assignmentId, ct)
                ?? new TaskTestSettings
                {
                    TaskAssignmentId = assignmentId,
                    MaxAttempts = 1,
                    PassPercent = 60,
                    ShuffleQuestions = true,
                    ShuffleAnswers = true,
                    AttemptTimeLimitsJson = "[]"
                };

            var questions = await _db.TaskTestQuestions
                .AsNoTracking()
                .Where(q => q.TaskAssignmentId == assignmentId)
                .OrderBy(q => q.Order)
                .ThenBy(q => q.Id)
                .ToListAsync(ct);

            if (questions.Count == 0)
                throw new ValidationException("В тесте пока нет вопросов");

            // Если у пользователя уже есть незавершённая попытка — возвращаем её,
            // чтобы можно было обновить страницу и продолжить.
            var activeAttempt = await _db.UserTaskTestAttempts
                .AsNoTracking()
                .Where(a => a.TaskAssignmentId == assignmentId && a.UserId == userId && a.SubmittedAt == null)
                .OrderByDescending(a => a.AttemptNumber)
                .FirstOrDefaultAsync(ct);

            if (activeAttempt != null)
            {
                var byId = questions.ToDictionary(q => q.Id, q => q);
                var orderIds = ParseGuidArray(activeAttempt.QuestionOrderJson);
                var orderedList = orderIds
                    .Select(id => byId.TryGetValue(id, out var q) ? q : null)
                    .Where(q => q != null)
                    .Cast<TaskTestQuestion>()
                    .ToList();
                if (orderedList.Count == 0) orderedList = questions;

                return new TaskTestStartResponseDto
                {
                    AttemptId = activeAttempt.Id,
                    AttemptNumber = activeAttempt.AttemptNumber,
                    MaxAttempts = settings.MaxAttempts <= 0 ? int.MaxValue : settings.MaxAttempts,
                    PassPercent = settings.PassPercent,
                    TimeLimitSeconds = activeAttempt.TimeLimitSeconds,
                    StartedAt = activeAttempt.StartedAt,
                    ShuffleQuestions = settings.ShuffleQuestions,
                    ShuffleAnswers = settings.ShuffleAnswers,
                    Questions = BuildPublicQuestions(activeAttempt.Id, settings, orderedList)
                };
            }

            var usedAttempts = await _db.UserTaskTestAttempts
                .AsNoTracking()
                .CountAsync(a => a.TaskAssignmentId == assignmentId && a.UserId == userId, ct);

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

            var questionOrder = questions.Select(q => q.Id).ToList();
            if (settings.ShuffleQuestions)
                Shuffle(questionOrder, new Random(SeedFromGuid(attemptId)));

            var attempt = new UserTaskTestAttempt
            {
                Id = attemptId,
                TaskAssignmentId = assignmentId,
                UserId = userId,
                AttemptNumber = attemptNumber,
                StartedAt = DateTime.UtcNow,
                TimeLimitSeconds = timeLimitSeconds,
                QuestionOrderJson = JsonSerializer.Serialize(questionOrder, JsonOptions)
            };

            _db.UserTaskTestAttempts.Add(attempt);
            await _db.SaveChangesAsync(ct);

            // строим public DTO в фактическом порядке
            var map = questions.ToDictionary(q => q.Id, q => q);
            var orderedQuestions = questionOrder
                .Where(map.ContainsKey)
                .Select(id => map[id])
                .ToList();

            return new TaskTestStartResponseDto
            {
                AttemptId = attempt.Id,
                AttemptNumber = attempt.AttemptNumber,
                MaxAttempts = settings.MaxAttempts,
                PassPercent = ClampPercent(settings.PassPercent),
                TimeLimitSeconds = timeLimitSeconds,
                StartedAt = attempt.StartedAt,
                ShuffleQuestions = settings.ShuffleQuestions,
                ShuffleAnswers = settings.ShuffleAnswers,
                Questions = BuildPublicQuestions(attemptId, settings, orderedQuestions)
            };
        }

        public async Task<TaskTestSubmitResultDto> SubmitAsync(Guid assignmentId, Guid userId, TaskTestSubmitRequestDto request, CancellationToken ct)
        {
            if (request.AttemptId == Guid.Empty)
                throw new ValidationException("AttemptId обязателен");

            var attempt = await _db.UserTaskTestAttempts
                .FirstOrDefaultAsync(a => a.Id == request.AttemptId && a.TaskAssignmentId == assignmentId && a.UserId == userId, ct);
            if (attempt == null) throw new KeyNotFoundException("Attempt not found");
            if (attempt.SubmittedAt != null) throw new ValidationException("Попытка уже отправлена");

            var settings = await _db.TaskTestSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TaskAssignmentId == assignmentId, ct)
                ?? new TaskTestSettings { TaskAssignmentId = assignmentId };

            var questions = await _db.TaskTestQuestions
                .AsNoTracking()
                .Where(q => q.TaskAssignmentId == assignmentId)
                .ToListAsync(ct);
            if (questions.Count == 0) throw new ValidationException("В тесте нет вопросов");

            var questionMap = questions.ToDictionary(q => q.Id, q => q);
            var orderedIds = SafeDeserialize<List<Guid>>(attempt.QuestionOrderJson) ?? new List<Guid>();
            if (orderedIds.Count == 0)
                orderedIds = questions.OrderBy(q => q.Order).Select(q => q.Id).ToList();

            var answerMap = (request.Answers ?? new List<TaskTestAnswerDto>())
                .GroupBy(a => a.QuestionId)
                .ToDictionary(g => g.Key, g => g.First());

            var total = 0;
            var correct = 0;

            foreach (var qid in orderedIds)
            {
                if (!questionMap.TryGetValue(qid, out var q)) continue;
                total++;
                answerMap.TryGetValue(qid, out var ans);

                if (IsCorrect(q, ans))
                    correct++;
            }

            var scorePercent = total == 0 ? 0 : (int)Math.Floor(correct * 100.0 / total);
            scorePercent = ClampPercent(scorePercent);

            var timeExpired = false;
            if (attempt.TimeLimitSeconds.HasValue && attempt.TimeLimitSeconds.Value > 0)
            {
                var deadline = attempt.StartedAt.AddSeconds(attempt.TimeLimitSeconds.Value);
                if (DateTime.UtcNow > deadline)
                    timeExpired = true;
            }

            var passPercent = ClampPercent(settings.PassPercent);
            var passed = !timeExpired && scorePercent >= passPercent;

            attempt.SubmittedAt = DateTime.UtcNow;
            attempt.ScorePercent = scorePercent;
            attempt.Passed = passed;
            attempt.TimeExpired = timeExpired;
            attempt.AnswersJson = JsonSerializer.Serialize(request, JsonOptions);

            await _db.SaveChangesAsync(ct);

            var maxAttempts = settings.MaxAttempts;
            return new TaskTestSubmitResultDto
            {
                AttemptId = attempt.Id,
                AttemptNumber = attempt.AttemptNumber,
                MaxAttempts = maxAttempts,
                PassPercent = passPercent,
                TotalQuestions = total,
                CorrectQuestions = correct,
                ScorePercent = scorePercent,
                TimeExpired = timeExpired,
                Passed = passed
            };
        }

        // ===== Editor =====
        public async Task<TaskTestEditDto> GetEditAsync(Guid assignmentId, Guid userId, CancellationToken ct)
        {
            // Проверка прав: только владелец курса
            var assignment = await _db.TaskAssignments
                .Include(a => a.Course)
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
            if (assignment == null) throw new KeyNotFoundException("Assignment not found");
            if (assignment.Course.OwnerId != userId) throw new UnauthorizedAccessException("Forbidden");

            var settings = await _db.TaskTestSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TaskAssignmentId == assignmentId, ct)
                ?? new TaskTestSettings { TaskAssignmentId = assignmentId };

            var questions = await _db.TaskTestQuestions
                .AsNoTracking()
                .Where(q => q.TaskAssignmentId == assignmentId)
                .OrderBy(q => q.Order)
                .ThenBy(q => q.Id)
                .ToListAsync(ct);

            var dto = new TaskTestEditDto
            {
                Settings = new TaskTestSettingsDto
                {
                    MaxAttempts = settings.MaxAttempts,
                    PassPercent = ClampPercent(settings.PassPercent),
                    ShuffleQuestions = settings.ShuffleQuestions,
                    ShuffleAnswers = settings.ShuffleAnswers,
                    AttemptTimeLimitsSeconds = ParseTimeLimits(settings.AttemptTimeLimitsJson)
                },
                Questions = new List<TaskTestQuestionEditDto>()
            };

            foreach (var q in questions)
            {
                var qDto = new TaskTestQuestionEditDto
                {
                    Id = q.Id,
                    Order = q.Order,
                    Type = q.Type,
                    Prompt = q.Prompt
                };

                if (IsSingleChoice(q.Type))
                {
                    var data = SafeDeserialize<SingleChoiceData>(q.DataJson) ?? new SingleChoiceData();
                    qDto.Options = data.Options.Select(o => new TaskTestOptionDto { Key = o.Key, Text = o.Text }).ToList();
                    qDto.CorrectOptionKeys = data.CorrectOptionKeys.ToList();
                }
                else
                {
                    var data = SafeDeserialize<TextAnswerData>(q.DataJson) ?? new TextAnswerData();
                    qDto.AcceptedAnswers = data.AcceptedAnswers.ToList();
                    qDto.CaseSensitive = data.CaseSensitive;
                    qDto.Trim = data.Trim;
                }

                dto.Questions.Add(qDto);
            }

            return dto;
        }

        public async Task SaveEditAsync(Guid assignmentId, Guid userId, TaskTestEditDto dto, CancellationToken ct)
        {
            var assignment = await _db.TaskAssignments
                .Include(a => a.Course)
                .FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
            if (assignment == null) throw new KeyNotFoundException("Assignment not found");
            if (assignment.Course.OwnerId != userId) throw new UnauthorizedAccessException("Forbidden");
            if (!string.Equals(assignment.Type, "test", StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Тип задания должен быть 'test'");

            var settings = await _db.TaskTestSettings
                .FirstOrDefaultAsync(s => s.TaskAssignmentId == assignmentId, ct);
            if (settings == null)
            {
                settings = new TaskTestSettings
                {
                    Id = Guid.NewGuid(),
                    TaskAssignmentId = assignmentId,
                    CreatedAt = DateTime.UtcNow
                };
                _db.TaskTestSettings.Add(settings);
            }

            var normalizedSettings = dto.Settings ?? new TaskTestSettingsDto();
            settings.MaxAttempts = normalizedSettings.MaxAttempts;
            settings.PassPercent = ClampPercent(normalizedSettings.PassPercent);
            settings.ShuffleQuestions = normalizedSettings.ShuffleQuestions;
            settings.ShuffleAnswers = normalizedSettings.ShuffleAnswers;
            settings.AttemptTimeLimitsJson = JsonSerializer.Serialize(
                (normalizedSettings.AttemptTimeLimitsSeconds ?? new List<int?>()),
                JsonOptions);
            settings.UpdatedAt = DateTime.UtcNow;

            // upsert questions
            var existing = await _db.TaskTestQuestions
                .Where(q => q.TaskAssignmentId == assignmentId)
                .ToListAsync(ct);
            var existingMap = existing.ToDictionary(q => q.Id, q => q);

            var incoming = dto.Questions ?? new List<TaskTestQuestionEditDto>();
            // нормализуем order
            var orderedIncoming = incoming
                .Select((q, idx) =>
                {
                    q.Order = idx;
                    q.Type = NormalizeType(q.Type);
                    q.Options ??= new List<TaskTestOptionDto>();
                    q.CorrectOptionKeys ??= new List<string>();
                    q.AcceptedAnswers ??= new List<string>();
                    return q;
                })
                .ToList();

            var incomingIds = orderedIncoming.Where(q => q.Id != Guid.Empty).Select(q => q.Id).ToHashSet();
            foreach (var old in existing)
            {
                if (!incomingIds.Contains(old.Id))
                    _db.TaskTestQuestions.Remove(old);
            }

            foreach (var qDto in orderedIncoming)
            {
                var id = qDto.Id == Guid.Empty ? Guid.NewGuid() : qDto.Id;
                if (!existingMap.TryGetValue(id, out var entity))
                {
                    entity = new TaskTestQuestion
                    {
                        Id = id,
                        TaskAssignmentId = assignmentId,
                        CreatedAt = DateTime.UtcNow
                    };
                    _db.TaskTestQuestions.Add(entity);
                }

                entity.Order = qDto.Order;
                entity.Type = NormalizeType(qDto.Type);
                entity.Prompt = (qDto.Prompt ?? "").Trim();
                entity.UpdatedAt = DateTime.UtcNow;

                if (string.IsNullOrWhiteSpace(entity.Prompt))
                    throw new ValidationException("Текст вопроса не может быть пустым");

                if (IsSingleChoice(entity.Type))
                {
                    if (qDto.Options == null || qDto.Options.Count < 2)
                        throw new ValidationException("В single-choice должно быть минимум 2 варианта");

                    // гарантируем ключи
                    var opts = qDto.Options
                        .Select((o, idx) => new Option
                        {
                            Key = string.IsNullOrWhiteSpace(o.Key) ? idx.ToString() : o.Key.Trim(),
                            Text = (o.Text ?? "").Trim()
                        })
                        .ToList();

                    if (opts.Any(o => string.IsNullOrWhiteSpace(o.Text)))
                        throw new ValidationException("Варианты ответов не могут быть пустыми");

                    var correctKeys = (qDto.CorrectOptionKeys ?? new List<string>())
                        .Where(k => !string.IsNullOrWhiteSpace(k))
                        .Select(k => k.Trim())
                        .Distinct()
                        .ToList();

                    if (correctKeys.Count == 0)
                        throw new ValidationException("Нужно выбрать правильный вариант");

                    entity.DataJson = JsonSerializer.Serialize(new SingleChoiceData
                    {
                        Options = opts,
                        CorrectOptionKeys = correctKeys
                    }, JsonOptions);
                }
                else
                {
                    var answers = (qDto.AcceptedAnswers ?? new List<string>())
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Select(a => a.Trim())
                        .Distinct()
                        .ToList();

                    if (answers.Count == 0)
                        throw new ValidationException("Нужно указать хотя бы один правильный ответ");

                    entity.DataJson = JsonSerializer.Serialize(new TextAnswerData
                    {
                        AcceptedAnswers = answers,
                        CaseSensitive = qDto.CaseSensitive ?? false,
                        Trim = qDto.Trim ?? true
                    }, JsonOptions);
                }
            }

            await _db.SaveChangesAsync(ct);
        }

        // ===== Helpers =====
        private static bool IsSingleChoice(string? type)
            => string.Equals(type, "single-choice", StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, "single", StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, "choice", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeType(string? type)
        {
            var t = (type ?? "").Trim().ToLowerInvariant();
            if (t.Contains("choice") || t.Contains("single")) return "single-choice";
            if (t.Contains("fill") || t.Contains("cloze") || t.Contains("gap")) return "fill";
            if (t.Contains("text") || t.Contains("word") || t.Contains("input")) return "text";
            return "single-choice";
        }

        private static List<int?> ParseTimeLimits(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<int?>();
            try
            {
                var v = JsonSerializer.Deserialize<List<int?>>(json, JsonOptions);
                return v ?? new List<int?>();
            }
            catch
            {
                // поддержка старого формата "120,90" если вдруг
                var parts = json.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var list = new List<int?>();
                foreach (var p in parts)
                    if (int.TryParse(p, out var n)) list.Add(n);
                return list;
            }
        }

        private static List<Guid> ParseGuidArray(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<Guid>();
            try
            {
                var v = JsonSerializer.Deserialize<List<Guid>>(json, JsonOptions);
                return v ?? new List<Guid>();
            }
            catch
            {
                return new List<Guid>();
            }
        }

        private static List<TaskTestQuestionPublicDto> BuildPublicQuestions(Guid attemptId, TaskTestSettings settings, List<TaskTestQuestion> orderedQuestions)
        {
            var result = new List<TaskTestQuestionPublicDto>(orderedQuestions.Count);

            for (var idx = 0; idx < orderedQuestions.Count; idx++)
            {
                var q = orderedQuestions[idx];
                var dto = new TaskTestQuestionPublicDto
                {
                    Id = q.Id,
                    Order = idx,
                    Type = q.Type,
                    Prompt = q.Prompt
                };

                if (IsSingleChoice(q.Type))
                {
                    var data = SafeDeserialize<SingleChoiceData>(q.DataJson) ?? new SingleChoiceData();
                    var opts = data.Options
                        .Select(o => new TaskTestOptionDto { Key = o.Key, Text = o.Text })
                        .ToList();

                    if (settings.ShuffleAnswers)
                    {
                        var rnd = new Random(SeedFromGuid(attemptId) ^ SeedFromGuid(q.Id));
                        Shuffle(opts, rnd);
                    }

                    dto.Options = opts;
                }

                result.Add(dto);
            }

            return result;
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
            // детерминированный seed
            var b = id.ToByteArray();
            unchecked
            {
                int s = 17;
                for (var i = 0; i < b.Length; i++)
                    s = (s * 31) + b[i];
                return s;
            }
        }

        private static T? SafeDeserialize<T>(string json) where T : class
        {
            try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
            catch { return null; }
        }

        private static bool IsCorrect(TaskTestQuestion q, TaskTestAnswerDto? ans)
        {
            if (IsSingleChoice(q.Type))
            {
                var data = SafeDeserialize<SingleChoiceData>(q.DataJson) ?? new SingleChoiceData();
                var selected = (ans?.SelectedOptionKey ?? "").Trim();
                if (string.IsNullOrWhiteSpace(selected)) return false;
                return data.CorrectOptionKeys.Any(k => string.Equals(k, selected, StringComparison.Ordinal));
            }
            else
            {
                var data = SafeDeserialize<TextAnswerData>(q.DataJson) ?? new TextAnswerData();
                var text = ans?.Text ?? "";
                if (data.Trim) text = text.Trim();

                if (!data.CaseSensitive) text = text.ToLowerInvariant();

                foreach (var a in data.AcceptedAnswers)
                {
                    var expected = a ?? "";
                    if (data.Trim) expected = expected.Trim();
                    if (!data.CaseSensitive) expected = expected.ToLowerInvariant();
                    if (expected == text) return true;
                }

                return false;
            }
        }

        // ===== Data-json contracts =====
        private sealed class SingleChoiceData
        {
            public List<Option> Options { get; set; } = new();
            public List<string> CorrectOptionKeys { get; set; } = new();
        }

        private sealed class Option
        {
            public string Key { get; set; } = "";
            public string Text { get; set; } = "";
        }

        private sealed class TextAnswerData
        {
            public List<string> AcceptedAnswers { get; set; } = new();
            public bool CaseSensitive { get; set; }
            public bool Trim { get; set; } = true;
        }
    }
}
