using System.Text.Json.Nodes;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Workflows.Executors;
using Xunit;

namespace TaskForge.AiAgent.Tests;

public sealed class DraftAuthorTutorialStyleTests
{
    [Fact]
    public void LearningBridgeTutorial_RewritesDryIoSectionsAndShortestPhrase()
    {
        var draft = new DraftSpec
        {
            AssignmentType = "code-test",
            Title = "Считать два числа и вывести сумму",
            Description = """
Это обучающее задание: научись читать два целых числа из ввода и выводить их сумму. Следуй шагам:
1. Прочитай первую строку: `var aStr = Console.ReadLine();`
2. Преобразуй её в число: `int a = int.Parse(aStr);`
3. Прочитай вторую строку и преобразуй: `var bStr = Console.ReadLine();` и `int b = int.Parse(bStr);`
4. Выведи сумму: `Console.WriteLine(a + b);`

Формат ввода
Две строки, в каждой — одно целое число.

Пример
Ввод:
2
3

Вывод:
5

Напиши самый короткий короткий рабочий вариант.
""",
            Language = "csharp",
            ReferenceSolution = """
using System;
class Program {
  static void Main() {
    var aStr = Console.ReadLine();
    var bStr = Console.ReadLine();
    int a = int.Parse(aStr!);
    int b = int.Parse(bStr!);
    Console.WriteLine(a + b);
  }
}
""",
            PublicTests =
            [
                new TestCaseSpec { Input = "2\n3\n", ExpectedOutput = "5\n", IsHidden = false },
                new TestCaseSpec { Input = "10\n-4\n", ExpectedOutput = "6\n", IsHidden = false }
            ],
            Extra = new JsonObject { ["bridgeSkillId"] = "parse-int-from-readline" }
        };

        var text = DraftAuthorExecutor.EnsureLearningBridgeTutorialStyle(draft, BuildBridge(), 0);

        Assert.Contains("Давай", text);
        Assert.Contains("Следуй шагам:", text);
        Assert.Contains("Запусти код и проверь", text);
        Assert.Contains("с новой строки", text);
        Assert.Contains("`var aStr = Console.ReadLine();`", text);
        Assert.Contains("`int a = int.Parse(aStr!);`", text);
        Assert.DoesNotContain("Формат ввода", text);
        Assert.DoesNotContain("Формат вывода", text);
        Assert.DoesNotContain("Ввод:", text);
        Assert.DoesNotContain("Вывод:", text);
        Assert.DoesNotContain("самый короткий", text.ToLowerInvariant());
        Assert.DoesNotContain("`2\n3`", text);
    }

    [Fact]
    public void LearningBridgeTutorial_KeepsSimpleCppWalkthroughStyle()
    {
        var draft = new DraftSpec
        {
            AssignmentType = "code-test",
            Title = "Первая программа",
            Description = "Сухое условие без шагов.",
            Language = "cpp",
            ReferenceSolution = """
#include <iostream>
using namespace std;
int main() {
    cout << "Hi";
}
""",
            PublicTests = [new TestCaseSpec { Input = "", ExpectedOutput = "Hi", IsHidden = false }],
            Extra = new JsonObject { ["bridgeSkillId"] = "program-structure" }
        };

        var text = DraftAuthorExecutor.EnsureLearningBridgeTutorialStyle(draft, BuildBridge(), 0);

        Assert.Contains("Следуй шагам:", text);
        Assert.Contains("`#include <iostream>`", text);
        Assert.Contains("`using namespace std;`", text);
        Assert.Contains("`int main() {`", text);
        Assert.Contains("`cout << \"Hi\";`", text);
        Assert.Contains("Запусти код", text);
    }

    private static CourseSkillBridgeContext BuildBridge()
    {
        return new CourseSkillBridgeContext(
            BeforeAssignmentId: Guid.Parse("41fa7640-3653-4c2e-85b8-15d9225aaff6"),
            PreviousTitle: "Задание 5",
            AnchorTitle: "Задание 6",
            RequestedSkills: ["console input"],
            AcquiredSkills: ["console output", "strings"],
            TargetSkills: ["console input", "parse int"],
            MissingBridgeSkills: ["console input", "parse int"],
            Neighborhood: [],
            IsBridgeRequest: true,
            BridgePlan:
            [
                new JsonObject
                {
                    ["step"] = 0,
                    ["skillId"] = "parse-int-from-readline",
                    ["titleHint"] = "Считать число из консоли",
                    ["introducedSkills"] = new JsonArray("парсинг целого из строки через `int.Parse`")
                }
            ],
            Source: "test");
    }
}
