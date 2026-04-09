using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;
using taskforge.Constants;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace taskforge.Services
{
    public sealed class AssignmentService : IAssignmentService
    {
        private readonly ApplicationDbContext _db;

        public AssignmentService(ApplicationDbContext db) => _db = db;

        public async Task<Guid> CreateAsync(Guid courseId, CreateAssignmentRequest req, Guid currentUserId)
        {
            var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == courseId)
                         ?? throw new InvalidOperationException("Курс не найден");

            var isOwner = course.OwnerId == currentUserId
                          || await _db.CourseOwners.AnyAsync(o => o.CourseId == courseId && o.UserId == currentUserId);
            if (!isOwner)
                throw new UnauthorizedAccessException("Only course owner can add assignments.");

            var maxSort = await _db.TaskAssignments
                .Where(a => a.CourseId == courseId)
                .Select(a => (int?)a.Sort)
                .MaxAsync() ?? -1;

            var normalizedType = TaskAssignmentTypes.Normalize(req.Type);
            ValidateAssignmentPayload(
                title: req.Title,
                description: req.Description,
                assignmentType: normalizedType,
                difficulty: req.Difficulty,
                rating: req.Rating,
                codeTestCases: req.TestCases?.Select(x => (x.Input, x.ExpectedOutput)).ToList(),
                imageReferenceKey: null,
                imageThreshold: null);

            var allowedCsv = NormalizeAllowedLanguagesCsv(req.AllowedLanguages, normalizedType);

                // Пустой список/NULL => ограничений нет (разрешены все поддерживаемые языки для типа задания).
                // Ошибка только если пользователь прислал НЕпустой список, но после нормализации не осталось ни одного поддерживаемого языка.
                if (req.AllowedLanguages != null && req.AllowedLanguages.Any(x => !string.IsNullOrWhiteSpace(x)) && string.IsNullOrWhiteSpace(allowedCsv))
                    throw new ValidationException("allowedLanguages contains no supported languages");

	            var entity = new TaskAssignment
            {
                Id = Guid.NewGuid(),
                CourseId = courseId,
                Title = (req.Title ?? string.Empty).Trim(),
                Description = req.Description,
                Difficulty = req.Difficulty,
                Rating = req.Rating ?? 1,
                Tags = req.Tags,
	                Type = normalizedType,
                Sort = maxSort + 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                AllowedLanguagesCsv = string.IsNullOrWhiteSpace(allowedCsv) ? null : allowedCsv,

                // code policy (per task)
                CodeForbiddenCallsJson = (normalizedType == TaskAssignmentTypes.CodeTest || normalizedType == TaskAssignmentTypes.ImageTest)
                    ? SerializeCallList(req.CodeForbiddenCalls)
                    : null,
                CodeRequiredCallsJson = (normalizedType == TaskAssignmentTypes.CodeTest || normalizedType == TaskAssignmentTypes.ImageTest)
                    ? SerializeCallList(req.CodeRequiredCalls)
                    : null,
            };

            if (entity.Type == TaskAssignmentTypes.ImageTest)
            {
                entity.ImageTestSimilarityThreshold = 90;
            }

	            // Test cases are stored only for code-test.
	            if (entity.Type == TaskAssignmentTypes.CodeTest && req.TestCases != null)
            {
                foreach (var tc in req.TestCases)
                {
                    entity.TestCases.Add(new TaskTestCase
                    {
                        Id = Guid.NewGuid(),
                        Input = tc.Input ?? string.Empty,
                        ExpectedOutput = tc.ExpectedOutput ?? string.Empty,
                        IsHidden = tc.IsHidden
                    });
                }
            }

            _db.TaskAssignments.Add(entity);
            await _db.SaveChangesAsync();
            return entity.Id;
        }

        public async Task<IList<AssignmentListItemDto>> GetByCourseAsync(Guid courseId, Guid currentUserId)
{
    return await _db.TaskAssignments
        .Where(a => a.CourseId == courseId)
        .OrderBy(a => a.Sort)
        .ThenByDescending(a => a.CreatedAt)
        .Select(a => new AssignmentListItemDto
        {
            Id = a.Id,
            Title = a.Title,
            Description = a.Description,
            Difficulty = a.Difficulty,
            Tags = a.Tags,
            CreatedAt = a.CreatedAt,
            // Важно: "решено" должно работать для всех типов заданий.
            // - code-test: Solution.PassedAllTests
            // - test: UserTaskTestAttempts.Passed
            // - image-test: UserImageTaskSolutions.Passed == true (как правило финальная отправка)
            SolvedByCurrentUser =
                a.Solutions.Any(s => s.UserId == currentUserId && s.PassedAllTests)
                || _db.UserTaskTestAttempts.Any(t => t.TaskAssignmentId == a.Id && t.UserId == currentUserId && t.Passed)
                || _db.UserImageTaskSolutions.Any(s => s.TaskAssignmentId == a.Id && s.UserId == currentUserId && s.Passed == true && s.IsTrial == false)
                || _db.UserTaskMathAttempts.Any(m => m.TaskAssignmentId == a.Id && m.UserId == currentUserId && m.Passed),
            Sort = a.Sort,
            CanEdit = a.Course.OwnerId == currentUserId
                      || _db.CourseOwners.Any(o => o.CourseId == a.CourseId && o.UserId == currentUserId)
        })
        .ToListAsync();
}

public async Task<AssignmentDetailsDto?> GetDetailsAsync(Guid assignmentId, Guid currentUserId)
{
    var a = await _db.TaskAssignments
        .AsNoTracking()
        .Include(x => x.TestCases)
        .Include(x => x.Course)
        .Include(x => x.Solutions.Where(s => s.UserId == currentUserId))
        .FirstOrDefaultAsync(x => x.Id == assignmentId);

    if (a == null) return null;

    var canEdit = a.Course.OwnerId == currentUserId
                  || await _db.CourseOwners.AnyAsync(o => o.CourseId == a.CourseId && o.UserId == currentUserId);

    var visibleCases = a.TestCases
        .Where(tc => canEdit || !tc.IsHidden)
        .OrderBy(tc => tc.Id)
        .Select(tc => new AssignmentTestCaseDto
        {
            Id = tc.Id,
            Input = tc.Input,
            ExpectedOutput = tc.ExpectedOutput,
            IsHidden = tc.IsHidden
        })
        .ToList();

    return new AssignmentDetailsDto
    {
        Id = a.Id,
        CourseId = a.CourseId,
        Title = a.Title,
        Description = a.Description,
        Difficulty = a.Difficulty,
        Rating = a.Rating,
        Tags = a.Tags,
        Type = a.Type,
        AllowedLanguages = ParseAllowedLanguages(a.AllowedLanguagesCsv, a.Type),
        CreatedAt = a.CreatedAt,
        PublicTestCount = a.TestCases.Count(x => !x.IsHidden),
        HiddenTestCount = canEdit ? a.TestCases.Count(x => x.IsHidden) : 0,
        SolvedByCurrentUser =
            a.Solutions.Any(s => s.PassedAllTests)
            || _db.UserTaskTestAttempts.Any(t => t.TaskAssignmentId == a.Id && t.UserId == currentUserId && t.Passed)
            || _db.UserImageTaskSolutions.Any(s => s.TaskAssignmentId == a.Id && s.UserId == currentUserId && s.Passed == true && s.IsTrial == false)
            || _db.UserTaskMathAttempts.Any(m => m.TaskAssignmentId == a.Id && m.UserId == currentUserId && m.Passed),
        TestCases = visibleCases,
        Sort = a.Sort,
        ImageTestReferenceKey = a.Type == "image-test" ? a.ImageTestReferenceKey : (canEdit ? a.ImageTestReferenceKey : null),
        ImageTestSimilarityThreshold = a.ImageTestSimilarityThreshold,
        CodeForbiddenCalls = canEdit ? DeserializeCallList(a.CodeForbiddenCallsJson) : new List<string>(),
        CodeRequiredCalls = canEdit ? DeserializeCallList(a.CodeRequiredCallsJson) : new List<string>(),
        CanEdit = canEdit
    };
}

        public async Task UpdateAsync(Guid assignmentId, Guid currentUserId, UpdateAssignmentRequest request)
        {
            var task = await _db.Set<TaskAssignment>()
                .Include(a => a.Course)
                .FirstOrDefaultAsync(a => a.Id == assignmentId);

            if (task == null)
                throw new KeyNotFoundException("Assignment not found");

            var isOwner = task.Course?.OwnerId == currentUserId
                          || await _db.CourseOwners.AnyAsync(o => o.CourseId == task.CourseId && o.UserId == currentUserId);
            if (!isOwner)
                throw new UnauthorizedAccessException("Only course owner can edit this assignment.");

	            task.Title = (request.Title ?? string.Empty).Trim();
	            task.Description = request.Description;
	            var normalizedType = TaskAssignmentTypes.Normalize(request.Type);
	            if (!TaskAssignmentTypes.IsSupported(normalizedType))
	                throw new ValidationException($"Unsupported assignment type: '{request.Type}'");

	            var isCodeTest = normalizedType == TaskAssignmentTypes.CodeTest;

            ValidateAssignmentPayload(
                title: request.Title,
                description: request.Description,
                assignmentType: normalizedType,
                difficulty: request.Difficulty,
                rating: request.Rating,
                codeTestCases: request.TestCases?.Select(x => (x.Input, x.ExpectedOutput)).ToList(),
                imageReferenceKey: request.ImageTestReferenceKey,
                imageThreshold: request.ImageTestSimilarityThreshold);

                var allowedCsv2 = NormalizeAllowedLanguagesCsv(request.AllowedLanguages, normalizedType);

                if (request.AllowedLanguages != null && request.AllowedLanguages.Any(x => !string.IsNullOrWhiteSpace(x)) && string.IsNullOrWhiteSpace(allowedCsv2))
                    throw new ValidationException("allowedLanguages contains no supported languages");

	            task.Type = normalizedType;
                task.AllowedLanguagesCsv = string.IsNullOrWhiteSpace(allowedCsv2) ? null : allowedCsv2;

            // code policy (per-task)
            if (task.Type == TaskAssignmentTypes.CodeTest || task.Type == TaskAssignmentTypes.ImageTest)
            {
                task.CodeForbiddenCallsJson = SerializeCallList(request.CodeForbiddenCalls);
                task.CodeRequiredCallsJson = SerializeCallList(request.CodeRequiredCalls);
            }
            else
            {
                task.CodeForbiddenCallsJson = null;
                task.CodeRequiredCallsJson = null;
            }
            // image-test поля
            if (task.Type == TaskAssignmentTypes.ImageTest)
            {
                task.ImageTestReferenceKey = request.ImageTestReferenceKey;
                task.ImageTestSimilarityThreshold = request.ImageTestSimilarityThreshold;
            }
            else
            {
                task.ImageTestReferenceKey = null;
                task.ImageTestSimilarityThreshold = null;
            }
            task.Tags = request.Tags?.Trim();
            task.Difficulty = request.Difficulty;
            task.Rating = request.Rating ?? 1;
            task.UpdatedAt = DateTime.UtcNow;

            await _db.Set<TaskTestCase>()
                .Where(tc => tc.TaskAssignmentId == task.Id)
                .ExecuteDeleteAsync();

	            if (isCodeTest && request.TestCases != null && request.TestCases.Count > 0)
            {
                var newCases = request.TestCases.Select(tc => new TaskTestCase
                {
                    Id = Guid.NewGuid(),
                    TaskAssignmentId = task.Id,
                    Input = tc.Input ?? string.Empty,
                    ExpectedOutput = tc.ExpectedOutput ?? string.Empty,
                    IsHidden = tc.IsHidden
                });

                await _db.Set<TaskTestCase>().AddRangeAsync(newCases);
            }

            await _db.SaveChangesAsync();
        }

        public async Task DeleteAsync(Guid assignmentId, Guid currentUserId)
        {
            var task = await _db.Set<TaskAssignment>()
                .Include(a => a.Course)
                .Include(a => a.TestCases)
                .FirstOrDefaultAsync(a => a.Id == assignmentId)
                ?? throw new KeyNotFoundException("Assignment not found");

            var isOwner = task.Course?.OwnerId == currentUserId
                          || await _db.CourseOwners.AnyAsync(o => o.CourseId == task.CourseId && o.UserId == currentUserId);
            if (!isOwner)
                throw new UnauthorizedAccessException("Only course owner can delete assignment.");

            _db.Remove(task);
            await _db.SaveChangesAsync();
        }

        public async Task UpdateSortAsync(Guid assignmentId, Guid currentUserId, int sort)
        {
            var task = await _db.Set<TaskAssignment>()
                .Include(a => a.Course)
                .FirstOrDefaultAsync(a => a.Id == assignmentId)
                ?? throw new KeyNotFoundException("Assignment not found");

            var isOwner = task.Course?.OwnerId == currentUserId
                          || await _db.CourseOwners.AnyAsync(o => o.CourseId == task.CourseId && o.UserId == currentUserId);
            if (!isOwner)
                throw new UnauthorizedAccessException("Only course owner can reorder assignment.");

            task.Sort = sort;
            task.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        public async Task<bool> PlaceAfterAssignmentAsync(Guid assignmentId, Guid? afterAssignmentId, Guid currentUserId)
        {
            var task = await _db.Set<TaskAssignment>()
                .Include(a => a.Course)
                .FirstOrDefaultAsync(a => a.Id == assignmentId)
                ?? throw new KeyNotFoundException("Assignment not found");

            var isOwner = task.Course?.OwnerId == currentUserId
                          || await _db.CourseOwners.AnyAsync(o => o.CourseId == task.CourseId && o.UserId == currentUserId);
            if (!isOwner)
                throw new UnauthorizedAccessException("Only course owner can reorder assignment.");

            if (afterAssignmentId.HasValue && afterAssignmentId.Value == assignmentId)
                return false;

            var ordered = await _db.TaskAssignments
                .Where(x => x.CourseId == task.CourseId)
                .OrderBy(x => x.Sort)
                .ThenBy(x => x.CreatedAt)
                .ToListAsync();

            var moving = ordered.FirstOrDefault(x => x.Id == assignmentId);
            if (moving == null)
                throw new KeyNotFoundException("Assignment not found in course");

            ordered.RemoveAll(x => x.Id == assignmentId);

            var insertIndex = 0;
            if (afterAssignmentId.HasValue)
            {
                var anchorIndex = ordered.FindIndex(x => x.Id == afterAssignmentId.Value);
                if (anchorIndex < 0)
                    return false;
                insertIndex = anchorIndex + 1;
            }

            ordered.Insert(insertIndex, moving);
            var now = DateTime.UtcNow;
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Sort = i;
                ordered[i].UpdatedAt = now;
            }

            await _db.SaveChangesAsync();
            return true;
        }

        private static void ValidateAssignmentPayload(
            string? title,
            string? description,
            string assignmentType,
            int difficulty,
            int? rating,
            IList<(string? Input, string? ExpectedOutput)>? codeTestCases,
            string? imageReferenceKey,
            double? imageThreshold)
        {
            if (string.IsNullOrWhiteSpace(title))
                throw new ValidationException("У задания должно быть название.");

            if (title.Trim().Length > 200)
                throw new ValidationException("Название задания не должно быть длиннее 200 символов.");

            if (string.IsNullOrWhiteSpace(description))
                throw new ValidationException("Заполните условие задания.");

            if (!TaskAssignmentTypes.IsSupported(assignmentType))
                throw new ValidationException($"Неподдерживаемый тип задания: '{assignmentType}'.");

            if (difficulty < 1 || difficulty > 3)
                throw new ValidationException("Сложность должна быть от 1 до 3.");

            if (rating.HasValue && rating.Value < 0)
                throw new ValidationException("Рейтинг задания не может быть отрицательным.");

            if (assignmentType == TaskAssignmentTypes.CodeTest)
            {
                if (codeTestCases == null || codeTestCases.Count == 0)
                    throw new ValidationException("Для code-test нужен хотя бы один тест-кейс.");

                var invalidIndex = codeTestCases
                    .Select((x, idx) => new { x, idx })
                    .FirstOrDefault(x => string.IsNullOrWhiteSpace(x.x.Input) || string.IsNullOrWhiteSpace(x.x.ExpectedOutput));

                if (invalidIndex != null)
                    throw new ValidationException($"Тест-кейс #{invalidIndex.idx + 1} должен содержать и Input, и Expected Output.");
            }

            if (assignmentType == TaskAssignmentTypes.ImageTest)
            {
                if (string.IsNullOrWhiteSpace(imageReferenceKey))
                    throw new ValidationException("Для image-test нужно загрузить эталонную картинку.");

                if (!imageThreshold.HasValue || imageThreshold.Value < 0 || imageThreshold.Value > 100)
                    throw new ValidationException("Порог совпадения для image-test должен быть от 0 до 100.");
            }
        }

        // ===== allowed languages helpers =====

        private static string? NormalizeAllowedLanguagesCsv(IEnumerable<string>? langs, string assignmentType)
        {
            if (langs == null)
                return null;

            var supported = GetSupportedLanguagesSet(assignmentType);
            var arr = langs
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeLanguage)
                .Where(x => x != null && supported.Contains(x))
                .Distinct()
                .ToArray();

            return arr.Length == 0 ? null : string.Join(",", arr!);
        }

        private static List<string>? ParseAllowedLanguages(string? csv, string assignmentType)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return null;

            var supported = GetSupportedLanguagesSet(assignmentType);
            var arr = csv
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => NormalizeLanguage(x))
                .Where(x => x != null && supported.Contains(x))
                .Distinct()
                .ToList();

            return arr.Count == 0 ? null : arr!;
        }

        private static HashSet<string> GetSupportedLanguagesSet(string assignmentType)
        {
            var t = TaskAssignmentTypes.Normalize(assignmentType);
            if (t == TaskAssignmentTypes.ImageTest)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "python",
                    "pascal",
                    "cpp"
                };
            }

            // code-test (и любые будущие code-типы): поддерживаем языки компилятора
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "cpp",
                "csharp",
                "python",
                "javascript",
                "java",
                "pascal"
            };
        }

        private static string? NormalizeLanguage(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return null;

            var v = s.Trim().ToLowerInvariant();
            if (v == "py" || v == "python") return "python";
            if (v == "pas" || v == "pascal" || v == "pascalabc" || v == "pascalabcnet" || v == "pascalabc.net") return "pascal";
            if (v == "cs" || v == "c#" || v == "csharp") return "csharp";
            if (v == "c++" || v == "cpp") return "cpp";
            if (v == "js" || v == "javascript") return "javascript";
            if (v == "java") return "java";
            return null;
        }
    

private static JsonDocument? SerializeCallList(IList<string>? list)
{
    if (list == null) return null;
    var cleaned = list.Where(x => !string.IsNullOrWhiteSpace(x))
                      .Select(x => x.Trim())
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToList();
    if (cleaned.Count == 0) return null;

    var bytes = JsonSerializer.SerializeToUtf8Bytes(cleaned);
    return JsonDocument.Parse(bytes);
}

private static List<string> DeserializeCallList(JsonDocument? json)
{
    if (json is null) return new List<string>();
    try
    {
        if (json.RootElement.ValueKind != JsonValueKind.Array) return new List<string>();
        return json.RootElement.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    catch
    {
        return new List<string>();
    }
}
}
}
