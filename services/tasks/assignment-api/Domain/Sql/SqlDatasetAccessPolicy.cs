namespace TaskForge.Tasks.Api.Domain.Sql;

/// <summary>Catalog policy. Learners get a projection only through an accessible assignment.</summary>
public static class SqlDatasetAccessPolicy
{
    public static bool CanManage(SqlDataset dataset, Guid? userId, bool isEditor, bool isAdmin)
        => userId.HasValue && userId.Value != Guid.Empty
            && (isAdmin || (isEditor && (dataset.AccessScope == SqlDatasetScopes.EditorLibrary
                || (dataset.AccessScope == SqlDatasetScopes.Private && dataset.OwnerUserId == userId))));

    public static bool CanReuse(SqlDataset dataset, Guid? userId, bool isEditor, bool isAdmin)
        => !dataset.IsArchived && CanManage(dataset, userId, isEditor, isAdmin);

    public static bool CanChangeSharing(SqlDataset dataset, Guid? userId, bool isEditor, bool isAdmin)
        => userId.HasValue && userId.Value != Guid.Empty
            && (isAdmin || (isEditor && dataset.OwnerUserId == userId));
}
