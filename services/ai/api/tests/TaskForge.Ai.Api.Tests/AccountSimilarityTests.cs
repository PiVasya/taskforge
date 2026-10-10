using TaskForge.Ai.Api.Services.AccountIntelligence;
using Xunit;

namespace TaskForge.Ai.Api.Tests;

public sealed class AccountSimilarityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly AccountLearningProfile NoLearning = new();

    [Fact]
    public void PrevalenceCountsDistinctAccounts_NotRepeatedLoginsOrTaskAttempts()
    {
        var assignment = Guid.NewGuid();
        var a = Account(0, devices: [" shared-device ", "SHARED-DEVICE"],
            tasks: [assignment, assignment], solutions: [assignment]);
        var b = Account(1, devices: ["shared-device"], solutions: [assignment]);
        var context = AccountSimilarityContext.FromAccounts([a, b, a]);

        Assert.Equal(2, context.DeviceUserCount("SHARED-DEVICE"));
        Assert.Equal(2, context.AssignmentUserCount(assignment));
    }

    [Fact]
    public void ClassroomBrowser_DoesNotScoreLikePrivateBrowser()
    {
        var accounts = Enumerable.Range(0, 20)
            .Select(i => Account(i, devices: ["classroom-computer", "CLASSROOM-COMPUTER"]))
            .ToArray();
        var sharedContext = AccountSimilarityContext.FromAccounts(accounts);
        var privateContext = AccountSimilarityContext.FromAccounts(accounts.Take(2));

        var shared = Analyze(accounts[0], accounts[1], sharedContext);
        var privatePair = Analyze(accounts[0], accounts[1], privateContext);
        var sharedEvidence = Assert.Single(shared.Evidence.Where(x => x.Code == "technical.device.shared-environment"));
        var privateEvidence = Assert.Single(privatePair.Evidence.Where(x => x.Code == "technical.device.distinctive.same"));

        Assert.Equal(1, sharedEvidence.Weight);
        Assert.Equal("weak", sharedEvidence.Strength);
        Assert.Contains("20", sharedEvidence.Detail);
        Assert.Equal(18, privateEvidence.Weight);
        Assert.DoesNotContain(privatePair.Evidence, x => x.Code == "technical.device.distinctive.repeated");
    }

    [Fact]
    public void MultipleClassroomDevices_DoNotAccumulateStrongEvidence()
    {
        var accounts = Enumerable.Range(0, 15)
            .Select(i => Account(i, devices: ["shared-1", "shared-2"]))
            .ToArray();
        var context = AccountSimilarityContext.FromAccounts(accounts);
        var evidence = Assert.Single(Analyze(accounts[0], accounts[1], context).Evidence
            .Where(x => x.Code.StartsWith("technical.device.", StringComparison.Ordinal)));

        Assert.Equal(1, evidence.Weight);
        Assert.Contains("15", evidence.Detail);
    }

    [Fact]
    public void PopularClassAssignments_HaveMinimalWeight_WhileRareAssignmentsRemainUseful()
    {
        var curriculum = Enumerable.Range(0, 35).Select(_ => Guid.NewGuid()).ToArray();
        var accounts = Enumerable.Range(0, 20)
            .Select(i => Account(i, tasks: curriculum, solutions: curriculum))
            .ToArray();
        var classroomContext = AccountSimilarityContext.FromAccounts(accounts);
        var rareCohort = accounts.Take(2).Concat(
            Enumerable.Range(20, 18).Select(i => Account(i))).ToArray();
        var rareContext = AccountSimilarityContext.FromAccounts(rareCohort);

        var classroom = Analyze(accounts[0], accounts[1], classroomContext);
        var rare = Analyze(accounts[0], accounts[1], rareContext);
        var commonEvidence = Assert.Single(classroom.Evidence.Where(x => x.Code == "activity.assignments.common-curriculum"));
        var rareEvidence = Assert.Single(rare.Evidence.Where(x => x.Code == "activity.assignments.distinctive.many"));

        Assert.Equal(1, commonEvidence.Weight);
        Assert.Equal(8, rareEvidence.Weight);
        Assert.Contains("35", commonEvidence.Detail);
        Assert.DoesNotContain(classroom.Evidence, x => x.Code == "activity.assignments.distinctive.many");
    }

    [Fact]
    public void SmallCohortDoesNotMistakeUniversalAssignmentsForRareOnes()
    {
        var curriculum = Enumerable.Range(0, 35).Select(_ => Guid.NewGuid()).ToArray();
        var a = Account(0, tasks: curriculum);
        var b = Account(1, solutions: curriculum);
        var result = Analyze(a, b, AccountSimilarityContext.FromAccounts([a, b]));

        Assert.Contains(result.Evidence, x => x.Code == "activity.assignments.common-curriculum" && x.Weight == 1);
        Assert.DoesNotContain(result.Evidence, x => x.Code == "activity.assignments.distinctive.many");
    }

    [Fact]
    public void OldAccountActiveAfterNewRegistration_IsNotAnAccountHandoff()
    {
        var old = Account(0, created: Now.AddDays(-300), lastActivity: Now.AddDays(-150), loginDays: 2);
        var newer = Account(1, created: Now.AddDays(-220), lastActivity: Now.AddDays(-1), loginDays: 4);

        var result = Analyze(old, newer, AccountSimilarityContext.FromAccounts([old, newer]));

        Assert.DoesNotContain(result.Evidence, x => x.Code.StartsWith("activity.account-handoff", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Evidence, x => x.Code.StartsWith("activity.new-account-now-dominant", StringComparison.Ordinal));
    }

    [Fact]
    public void OldAccountStoppedBeforeNewRegistration_AndNewWasUsed_EnablesHandoff()
    {
        var old = Account(0, created: Now.AddDays(-60), lastActivity: Now.AddDays(-10), loginDays: 5);
        var newer = Account(1, created: Now.AddDays(-9), lastActivity: Now.AddDays(-1), loginDays: 3);
        var context = AccountSimilarityContext.FromAccounts([old, newer]);

        var expected = Assert.Single(Analyze(old, newer, context).Evidence
            .Where(x => x.Code == "activity.account-handoff.sequential.2w"));
        var reverse = Assert.Single(Analyze(newer, old, context).Evidence
            .Where(x => x.Code == "activity.account-handoff.sequential.2w"));

        Assert.Equal(7, expected.Weight);
        Assert.Equal(expected.Weight, reverse.Weight);
    }

    [Fact]
    public void UnusedNewAccount_DoesNotCountAsHandoff()
    {
        var old = Account(0, created: Now.AddDays(-60), lastActivity: Now.AddDays(-10), loginDays: 4);
        var newer = Account(1, created: Now.AddDays(-9));
        var result = Analyze(old, newer, AccountSimilarityContext.FromAccounts([old, newer]));

        Assert.DoesNotContain(result.Evidence, x => x.Code.StartsWith("activity.account-handoff", StringComparison.Ordinal));
    }

    [Fact]
    public void BriefOneDayOldAccount_HasReducedHandoffWeight()
    {
        var old = Account(0, created: Now.AddDays(-4), lastActivity: Now.AddDays(-3), loginDays: 1);
        var newer = Account(1, created: Now.AddDays(-2), lastActivity: Now.AddDays(-1), loginDays: 2);
        var result = Analyze(old, newer, AccountSimilarityContext.FromAccounts([old, newer]));

        var evidence = Assert.Single(result.Evidence.Where(x => x.Code == "activity.account-handoff.sequential.2w"));
        Assert.Equal(3, evidence.Weight);
    }

    [Fact]
    public void SharedSchoolInfrastructure_AloneDoesNotReachDuplicateThreshold()
    {
        var curriculum = Enumerable.Range(0, 35).Select(_ => Guid.NewGuid()).ToArray();
        var accounts = Enumerable.Range(0, 20).Select(i => Account(i,
            created: Now.AddDays(-8).AddMinutes(i * 3),
            lastActivity: Now.AddDays(-1),
            loginDays: 3,
            devices: ["shared-school-device"],
            tasks: curriculum,
            ips: ["same-school-ip"],
            agents: ["same-school-browser"])).ToArray();
        var result = Analyze(accounts[0], accounts[1], AccountSimilarityContext.FromAccounts(accounts));

        Assert.True(result.FinalScore < 42, $"Classmates reached duplicate threshold: {result.FinalScore}");
        Assert.DoesNotContain(result.Evidence, x => x.Code.StartsWith("activity.account-handoff", StringComparison.Ordinal));
    }

    private static AccountPairAnalysis Analyze(AccountIntelligenceAccount a, AccountIntelligenceAccount b, AccountSimilarityContext context)
        => AccountSimilarityEngine.AnalyzePair(a, b, NoLearning, context, Now);

    private static AccountIntelligenceAccount Account(
        int index,
        DateTimeOffset? created = null,
        DateTimeOffset? lastActivity = null,
        int loginDays = 0,
        string[]? devices = null,
        Guid[]? tasks = null,
        Guid[]? solutions = null,
        string[]? ips = null,
        string[]? agents = null)
        => new()
        {
            Identity = new IdentitySnapshotItem
            {
                UserId = Guid.NewGuid(),
                FirstName = index == 0 ? "Илья" : index == 1 ? "Светлана" : $"Ученик{index}",
                LastName = index == 0 ? "Андриенко" : index == 1 ? "Казимирова" : $"Фамилия{index}",
                Login = index == 0 ? "ilyaandriienko" : index == 1 ? "svetlyachok" : $"testlearner{index}",
                CreatedAt = created ?? Now.AddDays(-50 - index),
                LastLoginAt = lastActivity,
                LoginCount = lastActivity.HasValue ? 1 : 0,
                LoginDays = loginDays,
                DeviceHashes = devices ?? [],
                IpHashes = ips ?? [],
                UserAgentHashes = agents ?? [],
            },
            Tasks = tasks is null ? null : new TasksSnapshotItem
            {
                AssignmentIds = tasks,
                TotalAttempts = tasks.Length,
            },
            Solutions = solutions is null ? null : new SolutionsSnapshotItem
            {
                AssignmentIds = solutions,
                TotalAttempts = solutions.Length,
            },
        };
}
