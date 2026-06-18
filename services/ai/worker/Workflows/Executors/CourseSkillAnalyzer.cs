using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TaskForge.AiAgent.Contracts;

namespace TaskForge.AiAgent.Workflows.Executors;

internal static partial class CourseSkillAnalyzer
{
    private static readonly SkillRule[] Rules =
    {
        new("program-structure", "каркас программы", new[] { "using system", "public class program", "static void main", "int main", "#include", "namespace" }),
        new("console-output", "вывод на экран", new[] { "console.writeline", "console.write", "cout", "printf", "print", "вывод", "выведите", "напечат" }),
        new("string-literals", "строковые литералы", new[] { "hello", "hi", "строка", "текст", "символ", "кавыч" }),
        new("variables", "переменные", new[] { "переменн", "тип", "значение переменной", @"re:(^|[^a-zа-я0-9_])var\s+", @"re:(^|[^a-zа-я0-9_])int\s+", @"re:(^|[^a-zа-я0-9_])string\s+", @"re:(^|[^a-zа-я0-9_])double\s+", @"re:(^|[^a-zа-я0-9_])bool\s+" }),
        new("arithmetic", "арифметика", new[] { "арифмет", "сумм", "слож", "разност", "произвед", "делен", "остат", "+", "-", "*", "/", "%" }),
        new("comparison", "сравнения", new[] { "сравн", "больше", "меньше", "равн", "не равн", ">", "<", "==", "!=" }),
        new("conditions", "условия", new[] { "re:(^|[^a-zа-я0-9_])if([^a-zа-я0-9_]|$)", "else", "услов", "если", "иначе", "ветв" }),
        new("console-input", "ввод с клавиатуры", new[] { "console.readline", "readline", "cin", "scanf", "input", "stdin", "ввод", "вводится", "введите", "считайте", "считать", "прочитай", "с клавиатур", "стандартного ввода" }),
        new("string-input", "строковый ввод", new[] { "строк", "string", "текст", "слово", "имя", "символ" }),
        new("numeric-parse", "преобразование ввода в число", new[] { "int.parse", "convert.toint32", "parse", "tryparse", "преобраз", "целое число", "число с клавиатуры" }),
        new("multi-line-input", "несколько строк ввода", new[] { "несколько строк", "с новой строки", "каждое число", "две строки", "три строки" }),
        new("split-input", "разбор одной строки на части", new[] { "split", "через пробел", "в одной строке", "одной строке", "раздел", "токен" }),
        new("loops", "циклы", new[] { "цикл", "re:(^|[^a-zа-я0-9_])for([^a-zа-я0-9_]|$)", "re:(^|[^a-zа-я0-9_])while([^a-zа-я0-9_]|$)", "do while", "повтор" }),
        new("arrays", "массивы", new[] { "массив", "array", "элемент", "индекс", "размер массива" }),
        new("collections", "коллекции", new[] { "коллекц", "list<", "dictionary", "map", "set", "vector", "список" }),
        new("methods", "методы и функции", new[] { "метод", "функци", "параметр", "return", "возвращает", "static int", "static string" }),
        new("classes", "классы и объекты", new[] { "класс", "объект", "constructor", "конструктор", "property", "свойств" }),
        new("exceptions", "обработка ошибок", new[] { "tryparse", "try catch", "try-catch", "exception", "исключ", "ошибк" }),
        new("files", "работа с файлами", new[] { "файл", "stream", "reader", "writer", "filesystem" }),
        new("linq", "LINQ", new[] { "linq", "select", "where", "orderby", "lambda", "лямбд" })
    };

    public static CourseSkillBridgeContext Analyze(JsonElement payload, string userText)
    {
        // Neutral fallback only. It intentionally does NOT infer skills or choose
        // an anchor by keyword/regex. The LLM course-skill-map stage is the
        // source of truth for pedagogy. This fallback exists so logs/artifacts
        // can still show the course outline when the LLM map fails.
        var candidates = new List<AssignmentSkillCandidate>();
        CollectAssignmentCandidates(payload, candidates, 0);
        var ordered = candidates
            .Where(x => !x.IsHidden && !x.IsAiDraft)
            .OrderBy(x => x.Index ?? int.MaxValue)
            .ThenBy(x => x.Sort ?? int.MaxValue)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => x with { Skills = new List<string>() })
            .ToList();

        var previous = ordered.LastOrDefault();
        return new CourseSkillBridgeContext(
            BeforeAssignmentId: null,
            PreviousTitle: previous?.Title,
            AnchorTitle: null,
            RequestedSkills: Array.Empty<string>(),
            AcquiredSkills: Array.Empty<string>(),
            TargetSkills: Array.Empty<string>(),
            MissingBridgeSkills: Array.Empty<string>(),
            Neighborhood: BuildNeighborhood(ordered, null),
            IsBridgeRequest: LooksLikeLearningBridgeRequest(userText),
            BridgePlan: Array.Empty<JsonObject>(),
            Source: "neutral-fallback",
            AnchorReason: "Structure-only fallback; no keyword/regex skill inference was used.");
    }

    public static bool LooksLikeLearningBridgeRequest(string text)
    {
        var t = Normalize(text);
        return ContainsAny(t, "обучал", "подготов", "перед", "до задач", "мостик", "лестниц", "серия", "набор", "несколько", "guided", "bridge", "prerequisite");
    }

    public static IReadOnlyList<string> DetectSkills(string? text)
    {
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized)) return Array.Empty<string>();

        return Rules
            .Where(rule => rule.Terms.Any(term => MatchesTerm(normalized, term)))
            .Select(rule => rule.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> DetectCanonicalSkillIds(string? text)
    {
        var normalized = NormalizeForSkillId(text);
        if (string.IsNullOrWhiteSpace(normalized)) return Array.Empty<string>();

        // LLMs often pass skill ids as Russian slugs, for example
        // "input-считать-одну-строку-из-консоли".  Keep the raw text for
        // concrete APIs such as int.Parse, but use a separator-normalized copy
        // for semantic Russian/English phrases.
        var searchable = Regex.Replace(normalized, @"[-_/]+", " ", RegexOptions.CultureInvariant);

        var result = new List<string>();
        void AddIf(bool condition, string id)
        {
            if (condition && !result.Contains(id, StringComparer.OrdinalIgnoreCase)) result.Add(id);
        }

        // Common LLM-produced bridge skill ids. Keep this as taxonomy aliases,
        // not task templates: a composite id expands to the atomic skills it
        // logically contains, so critic/author do not fight over aliases.
        AddIf(ContainsAny(searchable, "console input", "console-input", "console readline", "console readline echo"), "console-input-line");
        AddIf(ContainsAny(searchable, "parse int from readline", "readline parse int"), "console-input-line");
        AddIf(ContainsAny(searchable, "parse int from readline", "readline parse int"), "parse-int");
        AddIf(ContainsAny(searchable, "sum two ints from input", "sum two integers from input"), "console-input-line");
        AddIf(ContainsAny(searchable, "sum two ints from input", "sum two integers from input"), "parse-int");
        AddIf(ContainsAny(searchable, "sum two ints from input", "sum two integers from input"), "multi-line-input");
        AddIf(ContainsAny(searchable, "sum two ints from input", "sum two integers from input"), "arithmetic");

        AddIf(ContainsAny(searchable, "tryparse", "try parse", "валидац", "некоррект", "ошибк ввода", "безопасн"), "input-validation");
        AddIf(ContainsAny(searchable, "regex", "regular expression", "регулярн", "регулярные выражения"), "regex");
        AddIf(ContainsAny(searchable, "split", "split(", "stringsplitoptions", "разбить строку", "разбор строки", "разделить строку", "токен", "в одной строке", "через пробел"), "split-input");
        AddIf(ContainsAny(searchable, "две строки", "три строки", "несколько строк", "каждое на отдельной", "последовательн ввод", "нескольких значений"), "multi-line-input");

        // Do not map generic words like "parse" / "парсинг" to parse-int.
        // In bridge plans "сложный парсинг/регулярные выражения" means
        // advanced parsing must stay forbidden; it must not forbid a simple
        // int.Parse/Convert.ToInt32 step explicitly required by the plan.
        AddIf(ContainsAny(searchable,
            "int.parse", "int tryparse", "int.tryparse", "convert.toint32",
            "строки в int", "строку в int", "строку в число", "строки в число",
            "преобразовать в int", "преобразовать ее в число", "преобразовать её в число",
            "преобразовать строку в число", "преобразовать введенную строку", "преобразовать введённую строку",
            "целое число", "число из консоли", "распарсить число"), "parse-int");

        AddIf(ContainsAny(searchable,
            "console.readline", "readline", "stdin", "с клавиатур", "из консоли", "стандартного ввода",
            "читать входную строку", "прочитай строк", "прочитать строк", "считай строк", "считать строк",
            "считать одну строк", "считать одно значение", "прочитать одно значение", "ввод строк",
            "входную строку", "ввода данных", "ввод данных"), "console-input-line");
        AddIf(ContainsAny(searchable, "console.writeline", "console.write", "stdout", "вывод", "вывести", "напечат"), "console-output");
        AddIf(ContainsAny(searchable, "string.length", "длин", "length"), "string-length");
        // Keep variable detection precise.  Previously ContainsAny normalized
        // needles such as " int " to "int", so labels like "int.Parse" in
        // mustNotUse were incorrectly classified as the "variables" skill and
        // later removed variables from the allowed support skills.  A variable
        // is detected either by an explicit variable-related word or by a real
        // declaration-like token followed by an identifier.
        var hasVariableDeclaration = Regex.IsMatch(searchable,
            @"(^|[^a-zа-я0-9_])(var|int|string|double|bool)\??\s+[a-zа-я_][a-zа-я0-9_]*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        AddIf(ContainsAny(searchable, "переменн", "variable", "сохран") || hasVariableDeclaration, "variables");
        AddIf(ContainsAny(searchable, "арифмет", "сумм", "сложение", "сложить", "сложи", "прибав", "вычит", "умнож", "делен", "остат", "+1", " a +", " b +", "+ b", "+ a"), "arithmetic");
        AddIf(ContainsAny(searchable, "услов", "если", "иначе", " if ", " else "), "conditions");
        AddIf(ContainsAny(searchable, "цикл", " for ", " while ", "foreach", "do while"), "loops");
        AddIf(ContainsAny(searchable, "массив", "array", "элемент", "индекс"), "arrays");
        AddIf(ContainsAny(searchable, "строков", "литерал", "кавыч", "конкатенац", "интерполяц", "текст"), "strings");
        return result;
    }

    public static string NormalizeSkillId(string? text)
    {
        var direct = NormalizeForSkillId(text).Trim();
        if (string.IsNullOrWhiteSpace(direct)) return string.Empty;
        var detected = DetectCanonicalSkillIds(text);
        if (detected.Count > 0) return detected[0];

        var alias = direct switch
        {
            "input" or "stdin" or "readline" or "console input" or "console-input" or "console-readline" or "console-readline-echo" => "console-input-line",
            "output" or "stdout" => "console-output",
            "parse" or "int-parse" or "numeric-parse" or "parse-int-from-readline" => "parse-int",
            "string-input" => "console-input-line",
            "strings" => "string-literals",
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(alias)) return alias;

        if (Regex.IsMatch(direct, @"^[a-z][a-z0-9-]{2,}$", RegexOptions.CultureInvariant))
            return direct;

        var slug = Regex.Replace(direct, @"[^a-z0-9а-я+#<>.]+", "-").Trim('-');
        return slug.Length <= 60 ? slug : slug[..60].Trim('-');
    }

    private static JsonArray ToSkillIdArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var id in values.Select(NormalizeSkillId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            arr.Add(id);
        return arr;
    }

}
