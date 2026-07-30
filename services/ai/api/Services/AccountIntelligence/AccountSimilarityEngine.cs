using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TaskForge.Ai.Api.Services.AccountIntelligence;

internal static partial class AccountSimilarityEngine
{
    private static readonly Dictionary<string, string> GivenNameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["александр"] = "саша", ["александра"] = "саша", ["саша"] = "саша",
        ["екатерина"] = "катя", ["катерина"] = "катя", ["катя"] = "катя",
        ["елизавета"] = "лиза", ["лиза"] = "лиза",
        ["дмитрий"] = "дима", ["дима"] = "дима",
        ["михаил"] = "миша", ["миша"] = "миша",
        ["максим"] = "макс", ["макс"] = "макс",
        ["анастасия"] = "настя", ["настя"] = "настя",
        ["наталья"] = "наташа", ["наталия"] = "наташа", ["наташа"] = "наташа",
        ["николай"] = "коля", ["коля"] = "коля",
        ["владимир"] = "вова", ["вова"] = "вова",
        ["владислав"] = "влад", ["влад"] = "влад",
        ["евгений"] = "женя", ["евгения"] = "женя", ["женя"] = "женя",
        ["алексей"] = "леша", ["леша"] = "леша", ["лёша"] = "леша",
        ["сергей"] = "сережа", ["серёжа"] = "сережа", ["сережа"] = "сережа",
        ["павел"] = "паша", ["паша"] = "паша", ["иван"] = "ваня", ["ваня"] = "ваня",
        ["мария"] = "маша", ["маша"] = "маша", ["анна"] = "аня", ["аня"] = "аня",
        ["дарья"] = "даша", ["дария"] = "даша", ["даша"] = "даша",
        ["софия"] = "соня", ["софья"] = "соня", ["соня"] = "соня",
        ["виктория"] = "вика", ["вика"] = "вика", ["валерия"] = "лера", ["лера"] = "лера",
        ["ксения"] = "ксюша", ["ксюша"] = "ксюша", ["татьяна"] = "таня", ["таня"] = "таня",
        ["ольга"] = "оля", ["оля"] = "оля", ["светлана"] = "света", ["света"] = "света",
        ["ирина"] = "ира", ["ира"] = "ира", ["юлия"] = "юля", ["юля"] = "юля",
        ["роман"] = "рома", ["рома"] = "рома", ["даниил"] = "даня", ["данил"] = "даня", ["даня"] = "даня",
        ["тимофей"] = "тима", ["тима"] = "тима", ["всеволод"] = "сева", ["сева"] = "сева",
        ["андрей"] = "андрей", ["матвей"] = "матвей", ["артем"] = "артем", ["артём"] = "артем",
        ["кирилл"] = "кирилл", ["никита"] = "никита", ["илья"] = "илья", ["егор"] = "егор",
    };

    private static readonly HashSet<string> SuspiciousWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "тест", "tester", "testing", "admin", "administrator", "user", "username",
        "qwerty", "asdf", "asdfgh", "zxc", "zxczxc", "lol", "kek", "лол", "кек",
        "none", "null", "undefined", "unknown", "guest", "demo", "sample", "temp", "temporary",
        "aaa", "bbb", "abc", "abcd", "xxxx", "yyy", "no", "name", "noname", "имя", "фамилия"
    };

    private static readonly Dictionary<char, char> EnToRuKeyboard = new()
    {
        ['q']='й',['w']='ц',['e']='у',['r']='к',['t']='е',['y']='н',['u']='г',['i']='ш',['o']='щ',['p']='з',['[']='х',[']']='ъ',
        ['a']='ф',['s']='ы',['d']='в',['f']='а',['g']='п',['h']='р',['j']='о',['k']='л',['l']='д',[';']='ж',['\'']='э',
        ['z']='я',['x']='ч',['c']='с',['v']='м',['b']='и',['n']='т',['m']='ь',[',']='б',['.']='ю'
    };

    public static AccountPairAnalysis AnalyzePair(
        AccountIntelligenceAccount a,
        AccountIntelligenceAccount b,
        AccountLearningProfile learning,
        DateTimeOffset now)
    {
        var evidence = new List<AccountEvidence>();
        void Add(string code, string title, string detail, int weight, string strength = "medium", object? data = null)
        {
            if (weight <= 0 || evidence.Any(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))) return;
            evidence.Add(new AccountEvidence(code, title, detail, weight, strength, data));
        }

        var aName = NameForms(a.Identity.FirstName, a.Identity.LastName);
        var bName = NameForms(b.Identity.FirstName, b.Identity.LastName);

        if (aName.Full.Length > 2 && aName.Full == bName.Full)
            Add("name.exact", "Полное совпадение имени", $"«{a.DisplayName}» совпадает после нормализации.", 28, "very-strong");
        else if (aName.Full.Length > 2 && (aName.Full == bName.Reversed || aName.Reversed == bName.Full))
            Add("name.swapped", "Имя и фамилия переставлены", "Порядок имени и фамилии различается, но сами части совпадают.", 24, "very-strong");

        if (aName.Latin.Length > 2 && aName.Latin == bName.Latin)
            Add("name.transliteration.exact", "Совпадение после транслитерации", "Кириллица и латиница приводятся к одной записи.", 27, "very-strong");
        else if (aName.Latin.Length > 2 && (aName.Latin == bName.ReversedLatin || aName.ReversedLatin == bName.Latin))
            Add("name.transliteration.swapped", "Транслитерация с переставленными частями", "Имя и фамилия записаны другим алфавитом и в другом порядке.", 23, "very-strong");

        var visualA = NormalizeVisualConfusables(aName.Full);
        var visualB = NormalizeVisualConfusables(bName.Full);
        if (visualA.Length > 2 && visualA == visualB && aName.Full != bName.Full)
            Add("name.visual-confusables", "Смешаны похожие кириллические и латинские буквы", "После замены визуально одинаковых символов записи совпадают.", 12, "strong");

        if (aName.Keyboard.Length > 2 && (aName.Keyboard == bName.Full || bName.Keyboard == aName.Full))
            Add("name.keyboard-layout", "Исправляется раскладкой клавиатуры", "Одна запись похожа на имя, набранное в другой раскладке.", 14, "strong");

        var fullJaro = new[]
        {
            JaroWinkler(aName.Full, bName.Full),
            JaroWinkler(aName.Latin, bName.Latin),
            JaroWinkler(aName.Latin, bName.ReversedLatin),
            JaroWinkler(aName.ReversedLatin, bName.Latin),
        }.Max();
        var fullDice = new[]
        {
            TrigramDice(aName.Full, bName.Full),
            TrigramDice(aName.Latin, bName.Latin),
            TrigramDice(aName.Latin, bName.ReversedLatin),
            TrigramDice(aName.ReversedLatin, bName.Latin),
        }.Max();
        var fullEdit = new[]
        {
            NormalizedEditSimilarity(aName.Full, bName.Full),
            NormalizedEditSimilarity(aName.Latin, bName.Latin),
            NormalizedEditSimilarity(aName.Latin, bName.ReversedLatin),
            NormalizedEditSimilarity(aName.ReversedLatin, bName.Latin),
        }.Max();
        var combinedName = (fullJaro * 0.45) + (fullDice * 0.30) + (fullEdit * 0.25);
        if (combinedName >= 0.96)
            Add("name.fuzzy.96", "Почти идентичные имя и фамилия", $"Сходство имени: {combinedName:P0}.", 22, "very-strong", new { similarity = combinedName });
        else if (combinedName >= 0.90)
            Add("name.fuzzy.90", "Очень похожие имя и фамилия", $"Сходство имени: {combinedName:P0}.", 17, "strong", new { similarity = combinedName });
        else if (combinedName >= 0.82)
            Add("name.fuzzy.82", "Похожие имя и фамилия", $"Сходство имени: {combinedName:P0}.", 10, "medium", new { similarity = combinedName });
        else if (combinedName >= 0.74)
            Add("name.fuzzy.74", "Есть заметное сходство имени", $"Сходство имени: {combinedName:P0}.", 5, "weak", new { similarity = combinedName });

        var firstSimilarity = BestTokenSimilarity(aName.First, bName.First);
        var lastSimilarity = BestTokenSimilarity(aName.Last, bName.Last);
        if (firstSimilarity >= 0.94) Add("name.first.strong", "Совпадает имя", $"Сходство имени: {firstSimilarity:P0}.", 8, "strong");
        else if (firstSimilarity >= 0.84) Add("name.first.medium", "Похоже имя", $"Сходство имени: {firstSimilarity:P0}.", 4, "medium");
        if (lastSimilarity >= 0.95) Add("name.last.strong", "Совпадает фамилия", $"Сходство фамилии: {lastSimilarity:P0}.", 11, "strong");
        else if (lastSimilarity >= 0.86) Add("name.last.medium", "Похожа фамилия", $"Сходство фамилии: {lastSimilarity:P0}.", 6, "medium");

        var aliasA = CanonicalGivenName(aName.First);
        var aliasB = CanonicalGivenName(bName.First);
        if (aliasA.Length > 1 && aliasA == aliasB && aName.First != bName.First)
            Add("name.alias", "Полная и краткая форма имени", $"«{a.Identity.FirstName}» и «{b.Identity.FirstName}» относятся к одной форме имени.", 9, "strong");

        var loginA = NormalizeIdentifier(a.Identity.Login);
        var loginB = NormalizeIdentifier(b.Identity.Login);
        var emailA = NormalizeIdentifier(EmailLocalPart(a.Identity.Email));
        var emailB = NormalizeIdentifier(EmailLocalPart(b.Identity.Email));
        var loginLatinA = NormalizeIdentifier(Transliterate(a.Identity.Login ?? string.Empty));
        var loginLatinB = NormalizeIdentifier(Transliterate(b.Identity.Login ?? string.Empty));
        var emailLatinA = NormalizeIdentifier(Transliterate(EmailLocalPart(a.Identity.Email)));
        var emailLatinB = NormalizeIdentifier(Transliterate(EmailLocalPart(b.Identity.Email)));

        if (!string.IsNullOrWhiteSpace(a.Identity.Email) &&
            string.Equals(a.Identity.Email.Trim(), b.Identity.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
            Add("identity.email.exact", "Одинаковый email", "Полный адрес электронной почты совпадает.", 45, "hard");

        var loginSimilarity = MaxSimilarity(
            loginA, loginB, loginA, emailB, emailA, loginB, emailA, emailB,
            loginLatinA, loginLatinB, loginLatinA, emailLatinB, emailLatinA, loginLatinB, emailLatinA, emailLatinB);
        if (loginSimilarity >= 0.96)
            Add("identity.login.very-similar", "Почти одинаковые логины", $"Сходство логинов и адресов: {loginSimilarity:P0}.", 12, "strong");
        else if (loginSimilarity >= 0.86)
            Add("identity.login.similar", "Похожие логины", $"Сходство логинов и адресов: {loginSimilarity:P0}.", 6, "medium");

        if (LooksLikeNumberedCopy(loginA, loginB) || LooksLikeNumberedCopy(emailA, emailB) ||
            LooksLikeNumberedCopy(loginLatinA, loginLatinB) || LooksLikeNumberedCopy(emailLatinA, emailLatinB))
            Add("identity.numbered-copy", "Похоже на второй аккаунт", "Один логин отличается только цифрой или суффиксом вроде new/old.", 13, "strong");

        var phoneA = NormalizePhone(a.Identity.PhoneNumber);
        var phoneB = NormalizePhone(b.Identity.PhoneNumber);
        if (phoneA.Length >= 7 && phoneA == phoneB)
            Add("identity.phone.exact", "Одинаковый номер телефона", "Номер совпадает после удаления форматирования.", 43, "hard");
        else if (phoneA.Length >= 9 && phoneB.Length >= 9 && phoneA[^9..] == phoneB[^9..])
            Add("identity.phone.tail", "Совпадает номер без кода страны", "Последние 9 цифр номера совпадают.", 24, "very-strong");

        if (a.Identity.TelegramChatId.HasValue && a.Identity.TelegramChatId == b.Identity.TelegramChatId)
            Add("integration.telegram.chat-id", "Одна Telegram-учётная запись", "Совпадает подтверждённый Telegram chat ID.", 42, "hard");

        var tgA = NormalizeIdentifier(a.Identity.TelegramUsername ?? a.Identity.ProfileTelegram);
        var tgB = NormalizeIdentifier(b.Identity.TelegramUsername ?? b.Identity.ProfileTelegram);
        if (tgA.Length > 2 && tgA == tgB)
            Add("integration.telegram.exact", "Одинаковый Telegram", "У аккаунтов совпадает Telegram username.", 28, "hard");
        else if (MaxSimilarity(tgA, tgB) >= 0.92)
            Add("integration.telegram.similar", "Похожие Telegram username", "Telegram-имена очень похожи.", 8, "medium");

        var minecraftUuidsA = a.Minecraft?.Links.Select(x => NormalizeIdentifier(x.PlayerUuid)).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var minecraftUuidsB = b.Minecraft?.Links.Select(x => NormalizeIdentifier(x.PlayerUuid)).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        if (minecraftUuidsA.Overlaps(minecraftUuidsB))
            Add("integration.minecraft.uuid", "Один Minecraft-профиль", "Совпадает подтверждённый UUID игрока.", 42, "hard");

        var minecraftSimilarity = BestCollectionSimilarity(
            a.Minecraft?.Links.Select(x => x.PlayerName),
            b.Minecraft?.Links.Select(x => x.PlayerName));
        if (minecraftSimilarity >= 0.97)
            Add("integration.minecraft.similar", "Похожие Minecraft-профили", $"Сходство никнеймов: {minecraftSimilarity:P0}.", 10, "strong");
        else if (minecraftSimilarity >= 0.86)
            Add("integration.minecraft.weak", "Есть сходство Minecraft-никнеймов", $"Сходство никнеймов: {minecraftSimilarity:P0}.", 4, "weak");

        if (!string.IsNullOrWhiteSpace(a.Identity.ProfilePictureUrl) &&
            string.Equals(a.Identity.ProfilePictureUrl, b.Identity.ProfilePictureUrl, StringComparison.OrdinalIgnoreCase))
            Add("profile.avatar.exact", "Одинаковое изображение профиля", "Указан один и тот же URL аватара.", 7, "medium");
        if (SameNonEmpty(a.Identity.Github, b.Identity.Github))
            Add("profile.github.same", "Совпадает GitHub", a.Identity.Github!, 12, "strong");
        if (SameNonEmpty(a.Identity.Website, b.Identity.Website))
            Add("profile.website.same", "Совпадает личная ссылка", a.Identity.Website!, 6, "medium");

        if (SameNonEmpty(a.Identity.Education, b.Identity.Education))
            Add("profile.education.same", "Совпадает место обучения", a.Identity.Education!, 3, "weak");
        if (SameNonEmpty(a.Identity.Location, b.Identity.Location))
            Add("profile.location.same", "Совпадает местоположение", a.Identity.Location!, 2, "weak");

        var registrationGap = Math.Abs((a.Identity.CreatedAt - b.Identity.CreatedAt).TotalDays);
        if (registrationGap <= 1)
            Add("time.registration.1d", "Регистрация почти одновременно", $"Разница: {FormatDays(registrationGap)}.", 13, "strong");
        else if (registrationGap <= 7)
            Add("time.registration.1w", "Регистрация в течение недели", $"Разница: {FormatDays(registrationGap)}.", 12, "strong");
        else if (registrationGap <= 14)
            Add("time.registration.2w", "Регистрация в течение двух недель", $"Разница: {FormatDays(registrationGap)}.", 11, "strong");
        else if (registrationGap <= 30)
            Add("time.registration.1m", "Регистрация в течение месяца", $"Разница: {FormatDays(registrationGap)}.", 9, "medium");
        else if (registrationGap <= 45)
            Add("time.registration.6w", "Регистрация с разницей в несколько недель", $"Разница: {FormatDays(registrationGap)}.", 7, "medium");
        else if (registrationGap <= 60)
            Add("time.registration.2m", "Регистрация с разницей до двух месяцев", $"Разница: {FormatDays(registrationGap)}.", 5, "weak");
        else if (registrationGap <= 90)
            Add("time.registration.3m", "Регистрация с разницей до трёх месяцев", $"Разница: {FormatDays(registrationGap)}.", 3, "weak");

        var commonGroups = a.Groups.Select(x => x.GroupId).Intersect(b.Groups.Select(x => x.GroupId)).ToArray();
        if (commonGroups.Length > 0)
            Add("context.group.same", "Состоят в одной группе", string.Join(", ", a.Groups.Where(x => commonGroups.Contains(x.GroupId)).Select(x => x.Name ?? x.Code ?? x.GroupId.ToString())), Math.Min(11, 6 + commonGroups.Length * 2), "strong");

        var commonAssignments = AccountAssignmentIds(a).Intersect(AccountAssignmentIds(b)).Count();
        if (commonAssignments >= 12)
            Add("activity.assignments.many", "Большое пересечение заданий", $"Совпало заданий: {commonAssignments}.", 8, "medium");
        else if (commonAssignments >= 4)
            Add("activity.assignments.some", "Есть пересечение заданий", $"Совпало заданий: {commonAssignments}.", 4, "weak");

        var sharedDevices = SharedCount(a.Identity.DeviceHashes, b.Identity.DeviceHashes);
        if (sharedDevices >= 2)
            Add("technical.device.repeated", "Несколько общих устройств", $"Совпало устойчивых идентификаторов устройств: {sharedDevices}.", 24, "hard");
        else if (sharedDevices == 1)
            Add("technical.device.same", "Одно общее устройство", "Хотя бы один вход выполнен из одного браузерного профиля.", 18, "very-strong");

        var sharedIdentityIps = SharedCount(a.Identity.IpHashes, b.Identity.IpHashes);
        var sharedTaskIps = SharedCount(a.Tasks?.IpHashes, b.Tasks?.IpHashes);
        var sharedObservabilityIps = SharedCount(a.Observability?.IpHashes, b.Observability?.IpHashes);
        var sharedIpSources = new[] { sharedIdentityIps > 0, sharedTaskIps > 0, sharedObservabilityIps > 0 }.Count(x => x);
        var sharedIpTotal = sharedIdentityIps + sharedTaskIps + sharedObservabilityIps;
        if (sharedIpSources >= 2 && sharedIpTotal >= 3)
            Add("technical.ip.repeated", "IP совпадал в нескольких источниках", $"Повторяющихся сетевых следов: {sharedIpTotal}. Это всё равно слабая улика для сети технопарка.", 4, "weak");
        else if (sharedIpTotal > 0)
            Add("technical.ip.same", "Есть общий IP", "Это минимальная улика: в технопарке один IP может использоваться многими учениками.", 1, "weak");

        var sharedUa = SharedCount(a.Identity.UserAgentHashes, b.Identity.UserAgentHashes)
            + SharedCount(a.Tasks?.UserAgentHashes, b.Tasks?.UserAgentHashes)
            + SharedCount(a.Observability?.UserAgentHashes, b.Observability?.UserAgentHashes);
        if (sharedUa >= 3)
            Add("technical.user-agent.repeated", "Повторяется окружение браузера", $"Совпадений окружения: {sharedUa}.", 4, "weak");
        else if (sharedUa > 0)
            Add("technical.user-agent.same", "Совпадает тип браузера", "Очень слабая техническая улика.", 1, "weak");

        var aLast = LastActivity(a);
        var bLast = LastActivity(b);
        if (aLast.HasValue && bLast.HasValue && Math.Abs((aLast.Value - bLast.Value).TotalMinutes) <= 20)
            Add("activity.close-time", "Активность происходила рядом по времени", "Последняя заметная активность аккаунтов пришлась на близкий период.", 2, "weak");

        var olderAccount = a.Identity.CreatedAt <= b.Identity.CreatedAt ? a : b;
        var newerAccount = olderAccount.UserId == a.UserId ? b : a;
        var olderLast = LastActivity(olderAccount);
        if (olderLast.HasValue)
        {
            var handoffGap = Math.Abs((newerAccount.Identity.CreatedAt - olderLast.Value).TotalDays);
            if (handoffGap <= 14)
                Add("activity.account-handoff.2w", "Новый аккаунт появился рядом с активностью старого", $"Создание нового профиля и последняя активность старого разделены примерно на {FormatDays(handoffGap)}.", 7, "medium");
            else if (handoffGap <= 45)
                Add("activity.account-handoff.6w", "Возможный переход на новый аккаунт", $"Новый профиль появился в пределах нескольких недель от активности старого: {FormatDays(handoffGap)}.", 4, "weak");
        }

        var olderActivity = ActivityScore(olderAccount, now);
        var newerActivity = ActivityScore(newerAccount, now);
        if (newerActivity >= 55 && olderActivity <= 25 && newerActivity - olderActivity >= 30)
            Add("activity.new-account-now-dominant", "Новый аккаунт заметно активнее сейчас", $"Текущая активность: новый {newerActivity}/100, старый {olderActivity}/100.", 6, "medium");

        var baseScore = CappedEvidenceScore(evidence);
        var learnedDelta = Math.Clamp(
            evidence.Sum(x => learning.SignalAdjustments.GetValueOrDefault(x.Code)),
            -15.0,
            15.0);
        var learningBlend = learning.LabelCount switch { < 10 => 0.0, < 30 => 0.35, < 100 => 0.65, _ => 1.0 };
        var finalScore = Math.Clamp((int)Math.Round(baseScore + learnedDelta * learningBlend), 0, 100);
        var probability = 1.0 / (1.0 + Math.Exp(-(finalScore - 52) / 9.5));
        var suggested = ChooseSuggestedPrimary(a, b, now);

        return new AccountPairAnalysis
        {
            A = a,
            B = b,
            Evidence = evidence.OrderByDescending(x => x.Weight).ThenBy(x => x.Title).ToList(),
            BaseScore = baseScore,
            FinalScore = finalScore,
            Probability = probability,
            SuggestedPrimaryUserId = suggested,
        };
    }

    private static int CappedEvidenceScore(IEnumerable<AccountEvidence> evidence)
    {
        var caps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = 50,
            ["identity"] = 55,
            ["integration"] = 50,
            ["profile"] = 18,
            ["time"] = 15,
            ["context"] = 14,
            ["activity"] = 18,
            ["technical"] = 28,
        };

        var score = evidence
            .GroupBy(x => x.Code.Split('.', 2)[0], StringComparer.OrdinalIgnoreCase)
            .Sum(group => Math.Min(caps.GetValueOrDefault(group.Key, 15), group.Sum(x => x.Weight)));
        return Math.Clamp(score, 0, 100);
    }

    public static AccountSuspicionAnalysis AnalyzeSuspiciousAccount(AccountIntelligenceAccount account)
    {
        var evidence = new List<AccountEvidence>();
        void Add(string code, string title, string detail, int weight, string strength = "medium")
        {
            if (evidence.All(x => x.Code != code)) evidence.Add(new AccountEvidence(code, title, detail, weight, strength));
        }

        var first = NormalizeText(account.Identity.FirstName);
        var last = NormalizeText(account.Identity.LastName);
        var login = NormalizeIdentifier(account.Identity.Login);
        var full = NormalizeText($"{first} {last}");
        var tokens = full.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0) Add("suspicious.name.empty", "Не указано имя", "У аккаунта нет нормального имени и фамилии.", 35, "strong");
        if (tokens.Any(SuspiciousWords.Contains)) Add("suspicious.name.placeholder", "Похоже на тестовое имя", "В имени найдено слово-заглушка или интернет-сленг.", 34, "strong");
        if (first.Length > 1 && first == last) Add("suspicious.name.repeated", "Имя и фамилия одинаковые", $"Обе части: «{first}».", 20, "medium");
        if (tokens.Any(x => x.Length == 1)) Add("suspicious.name.one-char", "Слишком короткая часть имени", "Одна из частей состоит из одного символа.", 12, "weak");
        if (full.Count(char.IsDigit) >= Math.Max(2, full.Length / 2)) Add("suspicious.name.digits", "Слишком много цифр", "Имя больше похоже на техническую строку.", 22, "medium");
        if (HasLongRepeatedRun(full)) Add("suspicious.name.repeat-run", "Повторяющиеся символы", "Обнаружена длинная последовательность одинаковых символов.", 22, "medium");
        if (LooksRandom(full)) Add("suspicious.name.random", "Похоже на случайный набор", "В имени почти нет естественных гласных или слишком много редких сочетаний.", 18, "medium");
        if (SuspiciousWords.Contains(login)) Add("suspicious.login.placeholder", "Тестовый логин", $"Логин «{login}» похож на заглушку.", 24, "medium");
        if (account.Identity.LoginCount == 0 && TotalMeaningfulActions(account) == 0)
            Add("suspicious.activity.none", "Аккаунт ни разу не использовался", "Нет входов, решений и учебной активности.", 12, "weak");
        if (account.Identity.CreatedAt < DateTimeOffset.UtcNow.AddDays(-14) && TotalMeaningfulActions(account) == 0)
            Add("suspicious.activity.abandoned", "Создан и заброшен", "Прошло больше двух недель, но полезной активности нет.", 9, "weak");

        return new AccountSuspicionAnalysis
        {
            Account = account,
            Evidence = evidence.OrderByDescending(x => x.Weight).ToList(),
            Score = Math.Clamp(evidence.Sum(x => x.Weight), 0, 100),
        };
    }

    public static int ActivityScore(AccountIntelligenceAccount account, DateTimeOffset now)
    {
        var last = LastActivity(account);
        var days = last.HasValue ? Math.Max(0, (now - last.Value).TotalDays) : 9999;
        var recency = days switch
        {
            <= 1 => 55,
            <= 3 => 50,
            <= 7 => 45,
            <= 14 => 38,
            <= 30 => 30,
            <= 60 => 20,
            <= 90 => 12,
            <= 180 => 6,
            _ => 0,
        };
        var activeDays = account.Identity.LoginDays + (account.Tasks?.ActiveDays ?? 0) + (account.Solutions?.ActiveDays ?? 0) + (account.Observability?.ActiveDays ?? 0);
        var frequency = Math.Min(20, (int)Math.Round(Math.Log2(1 + activeDays) * 4));
        var meaningful = Math.Min(20, (int)Math.Round(Math.Log2(1 + TotalMeaningfulActions(account)) * 4));
        var breadth = Math.Min(5, account.Groups.Count + (account.Minecraft?.Links.Count ?? 0));
        return Math.Clamp(recency + frequency + meaningful + breadth, 0, 100);
    }

    public static int HistoricalValueScore(AccountIntelligenceAccount account)
    {
        var actions = TotalMeaningfulActions(account);
        var solved = (account.Solutions?.SolvedCount ?? 0) + (account.Tasks?.PassedAttempts ?? 0);
        var score = Math.Min(35, (int)Math.Round(Math.Log2(1 + actions) * 5));
        score += Math.Min(30, (int)Math.Round(Math.Log2(1 + solved) * 6));
        score += Math.Min(15, account.Solutions?.TotalScore / 20 ?? 0);
        score += account.Identity.TelegramLinkedAtUtc.HasValue ? 6 : 0;
        score += account.Minecraft?.Links.Count > 0 ? 6 : 0;
        score += account.Groups.Count > 0 ? 4 : 0;
        score += account.Identity.PhoneNumber is { Length: > 0 } ? 4 : 0;
        return Math.Clamp(score, 0, 100);
    }

    public static DateTimeOffset? LastActivity(AccountIntelligenceAccount account)
    {
        var values = new DateTimeOffset?[]
        {
            account.Identity.LastLoginAt,
            account.Identity.LastLoginLogAt,
            account.Tasks?.LastActivityAt,
            account.Solutions?.LastActivityAt,
            account.Solutions?.LastAcceptedAt,
            account.Observability?.LastActivityAt,
            account.Minecraft?.LastLinkedAtUtc,
        };
        var present = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
    }

    public static int TotalMeaningfulActions(AccountIntelligenceAccount account)
        => account.Identity.LoginCount
           + (account.Tasks?.TotalAttempts ?? 0)
           + (account.Tasks?.WorkSessions ?? 0)
           + (account.Solutions?.TotalAttempts ?? 0)
           + Math.Min(100, account.Observability?.PageViews ?? 0);

    public static IReadOnlyCollection<string> CandidateBlockKeys(AccountIntelligenceAccount account)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string prefix, string? value, int minimum = 3)
        {
            var normalized = NormalizeIdentifier(value);
            if (normalized.Length < minimum) return;
            keys.Add($"{prefix}:p3:{normalized[..Math.Min(3, normalized.Length)]}");
            if (normalized.Length >= 4) keys.Add($"{prefix}:p4:{normalized[..4]}");
            if (normalized.Length >= 5) keys.Add($"{prefix}:edge:{normalized[..2]}{normalized[^2..]}");
            var skeleton = PhoneticSkeleton(normalized);
            if (skeleton.Length >= 3) keys.Add($"{prefix}:phon:{skeleton[..Math.Min(5, skeleton.Length)]}");
        }

        var names = NameForms(account.Identity.FirstName, account.Identity.LastName);
        Add("first", names.First);
        Add("last", names.Last);
        Add("full", names.Full);
        Add("rev", names.Reversed);
        Add("latin", names.Latin);
        Add("latin-rev", names.ReversedLatin);
        Add("keyboard", names.Keyboard);
        Add("login", account.Identity.Login, 4);
        Add("email", EmailLocalPart(account.Identity.Email), 4);
        Add("login-latin", Transliterate(account.Identity.Login ?? string.Empty), 4);
        Add("email-latin", Transliterate(EmailLocalPart(account.Identity.Email)), 4);

        var phone = NormalizePhone(account.Identity.PhoneNumber);
        if (phone.Length >= 7)
        {
            var tailLength = Math.Min(9, phone.Length);
            keys.Add("phone:" + phone[^tailLength..]);
        }
        if (account.Identity.TelegramChatId.HasValue) keys.Add("telegram-id:" + account.Identity.TelegramChatId.Value);
        Add("telegram", account.Identity.TelegramUsername ?? account.Identity.ProfileTelegram, 3);
        foreach (var group in account.Groups.Take(16)) keys.Add("group:" + group.GroupId.ToString("N"));
        foreach (var hash in account.Identity.DeviceHashes.Where(x => !string.IsNullOrWhiteSpace(x)).Take(16)) keys.Add("device:" + hash);
        foreach (var hash in account.Identity.IpHashes.Where(x => !string.IsNullOrWhiteSpace(x)).Take(8)) keys.Add("ip:" + hash);
        foreach (var link in account.Minecraft?.Links.Take(8) ?? Enumerable.Empty<MinecraftLinkSnapshotItem>())
        {
            if (!string.IsNullOrWhiteSpace(link.PlayerUuid)) keys.Add("minecraft-uuid:" + NormalizeIdentifier(link.PlayerUuid));
            Add("minecraft-name", link.PlayerName, 3);
        }
        return keys;
    }

    public static IReadOnlyCollection<string> CandidateSortKeys(AccountIntelligenceAccount account)
    {
        var names = NameForms(account.Identity.FirstName, account.Identity.LastName);
        return new[]
        {
            names.Full, names.Reversed, names.Latin, names.ReversedLatin, names.Keyboard,
            NormalizeIdentifier(account.Identity.Login),
            NormalizeIdentifier(EmailLocalPart(account.Identity.Email)),
            NormalizeIdentifier(account.Identity.TelegramUsername ?? account.Identity.ProfileTelegram),
        }
        .Where(x => x.Length >= 3)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    public static string PairKey(Guid a, Guid b)
    {
        var values = new[] { a.ToString("N"), b.ToString("N") }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return $"pair:{values[0]}:{values[1]}";
    }

    public static string AccountKey(Guid userId) => $"account:{userId:N}";

    private static Guid ChooseSuggestedPrimary(AccountIntelligenceAccount a, AccountIntelligenceAccount b, DateTimeOffset now)
    {
        double Score(AccountIntelligenceAccount x)
        {
            var value = ActivityScore(x, now) * 0.45 + HistoricalValueScore(x) * 0.35;
            if (x.Verified) value += 25;
            if (x.Identity.TelegramLinkedAtUtc.HasValue) value += 5;
            if (x.Minecraft?.Links.Count > 0) value += 5;
            var ageDays = Math.Max(0, (now - x.Identity.CreatedAt).TotalDays);
            value += Math.Min(8, Math.Log10(1 + ageDays) * 3);
            return value;
        }
        return Score(a) >= Score(b) ? a.UserId : b.UserId;
    }

    private static IEnumerable<Guid> AccountAssignmentIds(AccountIntelligenceAccount account)
        => (account.Tasks?.AssignmentIds ?? []).Concat(account.Solutions?.AssignmentIds ?? []).Distinct();

    private static int SharedCount(IEnumerable<string?>? a, IEnumerable<string?>? b)
    {
        if (a == null || b == null) return 0;
        var left = a.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return b.Where(x => !string.IsNullOrWhiteSpace(x)).Count(x => left.Contains(x!));
    }

    private static bool SameNonEmpty(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var left = NormalizeText(a);
        var right = NormalizeText(b);
        return left == right || Transliterate(left) == Transliterate(right);
    }

    private static string FormatDays(double days)
        => days < 1 ? $"{Math.Max(1, (int)Math.Round(days * 24))} ч." : $"{Math.Round(days, 1):0.#} дн.";

    private static string CanonicalGivenName(string value)
    {
        var normalized = NormalizeText(value).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (normalized.Length == 0) return string.Empty;
        if (GivenNameAliases.TryGetValue(normalized, out var alias)) return Transliterate(alias);
        var latin = Transliterate(normalized);
        return GivenNameAliases.TryGetValue(latin, out alias) ? Transliterate(alias) : latin;
    }

    private static bool LooksLikeNumberedCopy(string a, string b)
    {
        if (a.Length < 3 || b.Length < 3 || a == b) return false;
        static string Strip(string value) => Regex.Replace(value, "(?:[-_.]?(?:new|old|copy|backup|alt|twink|twin|second|2nd|\\d+))+$", string.Empty, RegexOptions.IgnoreCase);
        var sa = Strip(a);
        var sb = Strip(b);
        return sa.Length >= 3 && sa == sb;
    }

    private static double BestCollectionSimilarity(IEnumerable<string?>? a, IEnumerable<string?>? b)
    {
        var left = a?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(NormalizeIdentifier).ToArray() ?? [];
        var right = b?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(NormalizeIdentifier).ToArray() ?? [];
        var best = 0.0;
        foreach (var x in left)
        foreach (var y in right)
            best = Math.Max(best, MaxSimilarity(x, y));
        return best;
    }

    private static double BestTokenSimilarity(string a, string b)
    {
        var aa = new[] { NormalizeText(a), Transliterate(NormalizeText(a)), CanonicalGivenName(a) }.Where(x => x.Length > 0).Distinct().ToArray();
        var bb = new[] { NormalizeText(b), Transliterate(NormalizeText(b)), CanonicalGivenName(b) }.Where(x => x.Length > 0).Distinct().ToArray();
        var best = 0.0;
        foreach (var x in aa)
        foreach (var y in bb)
            best = Math.Max(best, MaxSimilarity(x, y));
        return best;
    }

    private static double MaxSimilarity(params string[] pairs)
    {
        if (pairs.Length < 2) return 0;
        var best = 0.0;
        for (var i = 0; i + 1 < pairs.Length; i += 2)
        {
            var a = pairs[i];
            var b = pairs[i + 1];
            if (a.Length < 2 || b.Length < 2) continue;
            best = Math.Max(best, (JaroWinkler(a, b) * 0.55) + (TrigramDice(a, b) * 0.20) + (NormalizedEditSimilarity(a, b) * 0.25));
        }
        return best;
    }

    private sealed record NameForm(string First, string Last, string Full, string Reversed, string Latin, string ReversedLatin, string Keyboard);

    private static NameForm NameForms(string? first, string? last)
    {
        var f = NormalizeText(first);
        var l = NormalizeText(last);
        var full = NormalizeText($"{f} {l}");
        var reversed = NormalizeText($"{l} {f}");
        return new NameForm(
            f,
            l,
            full,
            reversed,
            Transliterate(full),
            Transliterate(reversed),
            NormalizeText(ConvertKeyboard(full)));
    }

    private static string EmailLocalPart(string? email)
    {
        var value = (email ?? string.Empty).Trim();
        var at = value.IndexOf('@');
        return at > 0 ? value[..at] : value;
    }

    private static string PhoneticSkeleton(string value)
    {
        var latin = Transliterate(value).Replace(" ", string.Empty, StringComparison.Ordinal);
        const string vowels = "aeiouy";
        var sb = new StringBuilder(latin.Length);
        char previous = '\0';
        foreach (var ch in latin)
        {
            if (!char.IsLetterOrDigit(ch) || vowels.Contains(ch) || ch == previous) continue;
            sb.Append(ch);
            previous = ch;
        }
        return sb.ToString();
    }

    private static string NormalizePhone(string? value)
        => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private static string NormalizeIdentifier(string? value)
        => NormalizeText(value).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        var lastSpace = false;
        foreach (var raw in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;
            var ch = raw switch
            {
                'ё' => 'е', 'і' => 'и', 'ї' => 'и', 'ў' => 'у',
                _ => raw,
            };
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastSpace = false;
            }
            else if (!lastSpace && sb.Length > 0)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }
        return Regex.Replace(sb.ToString().Trim(), "\\s+", " ");
    }

    private static string NormalizeVisualConfusables(string value)
    {
        var normalized = NormalizeText(value);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            sb.Append(ch switch
            {
                'а' => 'a', 'с' => 'c', 'е' => 'e', 'о' => 'o', 'р' => 'p',
                'х' => 'x', 'у' => 'y', 'к' => 'k', 'м' => 'm', 'т' => 't',
                'в' => 'b', 'н' => 'h',
                _ => ch,
            });
        }
        return sb.ToString();
    }

    private static string ConvertKeyboard(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            var lower = char.ToLowerInvariant(ch);
            sb.Append(EnToRuKeyboard.GetValueOrDefault(lower, lower));
        }
        return sb.ToString();
    }

    private static string Transliterate(string value)
    {
        var normalized = NormalizeText(value)
            .Replace("ье", "ye", StringComparison.Ordinal)
            .Replace("ья", "ya", StringComparison.Ordinal)
            .Replace("ью", "yu", StringComparison.Ordinal)
            .Replace("ьи", "yi", StringComparison.Ordinal);
        var map = new Dictionary<char, string>
        {
            ['а']="a",['б']="b",['в']="v",['г']="g",['д']="d",['е']="e",['ё']="e",['ж']="zh",['з']="z",['и']="i",['й']="y",
            ['к']="k",['л']="l",['м']="m",['н']="n",['о']="o",['п']="p",['р']="r",['с']="s",['т']="t",['у']="u",['ф']="f",
            ['х']="h",['ц']="ts",['ч']="ch",['ш']="sh",['щ']="sch",['ъ']="",['ы']="y",['ь']="",['э']="e",['ю']="yu",['я']="ya",
        };
        var sb = new StringBuilder(normalized.Length * 2);
        foreach (var ch in normalized) sb.Append(map.GetValueOrDefault(ch, ch.ToString()));
        return Regex.Replace(sb.ToString(), "\\s+", " ").Trim();
    }

    private static double NormalizedEditSimilarity(string a, string b)
    {
        if (a == b && a.Length > 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var distance = DamerauLevenshtein(a, b);
        return Math.Max(0, 1.0 - (double)distance / Math.Max(a.Length, b.Length));
    }

    private static int DamerauLevenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
        {
            var cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + cost);
        }
        return d[a.Length, b.Length];
    }

    private static double TrigramDice(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return a == b && a.Length > 0 ? 1 : 0;
        var left = Ngrams(a, 3);
        var right = Ngrams(b, 3);
        var overlap = left.Intersect(right).Count();
        return (2.0 * overlap) / Math.Max(1, left.Count + right.Count);
    }

    private static HashSet<string> Ngrams(string value, int n)
    {
        var padded = $"^{value}$";
        if (padded.Length <= n) return [padded];
        return Enumerable.Range(0, padded.Length - n + 1).Select(i => padded.Substring(i, n)).ToHashSet(StringComparer.Ordinal);
    }

    private static double JaroWinkler(string s1, string s2)
    {
        if (s1 == s2) return s1.Length == 0 ? 0 : 1;
        if (s1.Length == 0 || s2.Length == 0) return 0;
        var matchDistance = Math.Max(s1.Length, s2.Length) / 2 - 1;
        var s1Matches = new bool[s1.Length];
        var s2Matches = new bool[s2.Length];
        var matches = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            var start = Math.Max(0, i - matchDistance);
            var end = Math.Min(i + matchDistance + 1, s2.Length);
            for (var j = start; j < end; j++)
            {
                if (s2Matches[j] || s1[i] != s2[j]) continue;
                s1Matches[i] = true;
                s2Matches[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0;
        var k = 0;
        var transpositions = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            if (!s1Matches[i]) continue;
            while (!s2Matches[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }
        var m = (double)matches;
        var jaro = (m / s1.Length + m / s2.Length + (m - transpositions / 2.0) / m) / 3.0;
        var prefix = 0;
        for (var i = 0; i < Math.Min(4, Math.Min(s1.Length, s2.Length)) && s1[i] == s2[i]; i++) prefix++;
        return jaro + prefix * 0.1 * (1 - jaro);
    }

    private static bool HasLongRepeatedRun(string value)
        => Regex.IsMatch(value.Replace(" ", string.Empty), "(.)\\1{3,}", RegexOptions.IgnoreCase);

    private static bool LooksRandom(string value)
    {
        var letters = value.Where(char.IsLetter).ToArray();
        if (letters.Length < 5) return false;
        const string vowels = "аеёиоуыэюяaeiouy";
        var vowelCount = letters.Count(x => vowels.Contains(char.ToLowerInvariant(x)));
        return vowelCount == 0 || (double)vowelCount / letters.Length < 0.12;
    }
}
