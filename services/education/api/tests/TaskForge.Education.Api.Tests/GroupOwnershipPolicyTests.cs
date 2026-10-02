using TaskForge.Education.Api.Contracts;
using TaskForge.Education.Api.Services.Common;
using Xunit;

namespace TaskForge.Education.Api.Tests;

public sealed class GroupOwnershipPolicyTests
{
    [Fact]
    public void Admin_CanManageGroupOnlyWhenOwner()
    {
        var adminId = Guid.NewGuid();
        var access = new EducationAccessContext(adminId, true, false, new HashSet<Guid>(), 800);

        Assert.True(EducationApiCommonService.CanManageGroup(access, true));
        Assert.False(EducationApiCommonService.CanManageGroup(access, false));
    }

    [Fact]
    public void SuperAdmin_CanManageGroupWithoutOwnership()
    {
        var superAdminId = Guid.NewGuid();
        var access = new EducationAccessContext(superAdminId, true, false, new HashSet<Guid>(), 1000, true);

        Assert.True(EducationApiCommonService.CanManageGroup(access, false));
    }

    [Fact]
    public void Editor_CannotManageGroupEvenIfListedAsOwner()
    {
        var editorId = Guid.NewGuid();
        var access = new EducationAccessContext(editorId, true, false, new HashSet<Guid>(), 400);

        Assert.False(EducationApiCommonService.CanManageGroup(access, true));
    }

    [Fact]
    public void OnlySuperAdmin_CanChangeGroupOwnerList()
    {
        var admin = new EducationAccessContext(Guid.NewGuid(), true, false, new HashSet<Guid>(), 800);
        var superAdmin = new EducationAccessContext(Guid.NewGuid(), true, false, new HashSet<Guid>(), 1000, true);

        Assert.False(EducationApiCommonService.CanChangeGroupOwners(admin));
        Assert.True(EducationApiCommonService.CanChangeGroupOwners(superAdmin));
    }

    [Fact]
    public void AnonymousUser_CannotManageGroup()
    {
        var access = new EducationAccessContext(null, false, false, new HashSet<Guid>(), 0);

        Assert.False(EducationApiCommonService.CanManageGroup(access, true));
    }
}
