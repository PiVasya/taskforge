using TaskForge.Education.Api.Contracts;
using TaskForge.Education.Api.Domain;
using TaskForge.Education.Api.Services.Common;
using Xunit;

namespace TaskForge.Education.Api.Tests;

public sealed class CourseAccessPolicyTests
{
    [Fact]
    public void CourseOwnerWithoutEditorRole_CanViewPrivateCourseButCannotEditIt()
    {
        var userId = Guid.NewGuid();
        var course = new Course
        {
            Id = Guid.NewGuid(),
            IsPublic = false,
            IsHiddenFromStudents = false,
            OwnerIdsJson = $"[\"{userId:D}\"]"
        };
        var access = new EducationAccessContext(userId, false, false, new HashSet<Guid>());

        Assert.True(EducationApiCommonService.CanViewCourse(access, course));
        Assert.False(EducationApiCommonService.CanEditCourse(access, course));
    }

    [Fact]
    public void EditorRole_CanEditCourseWithoutBeingListedAsOwner()
    {
        var course = new Course
        {
            Id = Guid.NewGuid(),
            OwnerIdsJson = "[]"
        };
        var access = new EducationAccessContext(Guid.NewGuid(), true, false, new HashSet<Guid>());

        Assert.True(EducationApiCommonService.CanEditCourse(access, course));
    }

    [Fact]
    public void StudentVisibilityBypass_DoesNotGrantCourseEditPermission()
    {
        var course = new Course
        {
            Id = Guid.NewGuid(),
            IsPublic = false,
            IsHiddenFromStudents = true,
            OwnerIdsJson = "[]"
        };
        var access = new EducationAccessContext(Guid.NewGuid(), false, true, new HashSet<Guid>());

        Assert.True(EducationApiCommonService.CanViewCourse(access, course));
        Assert.False(EducationApiCommonService.CanEditCourse(access, course));
    }
}
