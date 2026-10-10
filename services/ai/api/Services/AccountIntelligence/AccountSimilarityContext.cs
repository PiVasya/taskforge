namespace TaskForge.Ai.Api.Services.AccountIntelligence;

// Per-run prevalence is computed across distinct accounts, never from the number of logs
// or from individual task/solution attempts. A shared school computer or curriculum
// must not become stronger evidence merely because pupils use it frequently.
internal sealed class AccountSimilarityContext
{
    private readonly Dictionary<string, int> deviceUsers;
    private readonly Dictionary<Guid, int> assignmentUsers;

    public int AccountCount { get; }

    private AccountSimilarityContext(
        int accountCount,
        Dictionary<string, int> deviceUsers,
        Dictionary<Guid, int> assignmentUsers)
    {
        AccountCount = accountCount;
        this.deviceUsers = deviceUsers;
        this.assignmentUsers = assignmentUsers;
    }

    public static AccountSimilarityContext FromAccounts(IEnumerable<AccountIntelligenceAccount> accounts)
    {
        var deviceUsers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var assignmentUsers = new Dictionary<Guid, int>();

        var distinctAccounts = accounts.DistinctBy(x => x.UserId).ToArray();
        foreach (var account in distinctAccounts)
        {
            foreach (var hash in account.Identity.DeviceHashes
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                deviceUsers[hash] = deviceUsers.GetValueOrDefault(hash) + 1;
            }

            foreach (var id in AccountSimilarityEngine.AccountAssignmentIds(account))
            {
                assignmentUsers[id] = assignmentUsers.GetValueOrDefault(id) + 1;
            }
        }

        return new AccountSimilarityContext(distinctAccounts.Length, deviceUsers, assignmentUsers);
    }

    public int DeviceUserCount(string? hash)
        => string.IsNullOrWhiteSpace(hash) ? 0 : deviceUsers.GetValueOrDefault(hash.Trim());

    public int AssignmentUserCount(Guid assignmentId)
        => assignmentUsers.GetValueOrDefault(assignmentId);
}
