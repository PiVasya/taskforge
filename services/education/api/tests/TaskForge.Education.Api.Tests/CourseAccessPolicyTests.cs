using TaskForge.Education.Api.Contracts;
using TaskForge.Education.Api.Domain;
using TaskForge.Education.Api.Services.Common;
using Xunit;

namespace TaskForge.Education.Api.Tests;

public sealed class CourseAccessPolicyTests
{
    private static Course CourseOwnedBy(params Guid[] owners) => new()
    {
        Id = Guid.NewGuid(),
        OwnerIdsJson = System.Text.Json.JsonSerializer.Serialize(owners)
    };

    [Fact]
    public void CourseOwnerWithoutEditorRole_CanViewPrivateCourseButCannotEditIt()
    {
        var userId = Guid.NewGuid();
        var course = CourseOwnedBy(userId);
        course.IsPublic = false;
        course.IsHiddenFromStudents = false;
        var access = new EducationAccessContext(userId, false, false, new HashSet<Guid>());

        Assert.True(EducationApiCommonService.CanViewCourse(access, course));
        Assert.False(EducationApiCommonService.CanEditCourse(access, course));
    }

    [Fact]
    public void Editor_CanEditOwnCourse()
    {
        var editor = Guid.NewGuid();
        var course = CourseOwnedBy(editor);
        var access = new EducationAccessContext(editor, true, false, new HashSet<Guid>(), 400, false, new Dictionary<Guid, int> { [editor] = 400 });

        Assert.True(EducationApiCommonService.CanEditCourse(access, course));
    }

    [Fact]
    public void Editor_CanEditCourseOwnedOnlyByLowerRole()
    {
        var editor = Guid.NewGuid();
        var user = Guid.NewGuid();
        var course = CourseOwnedBy(user);
        var access = new EducationAccessContext(editor, true, false, new HashSet<Guid>(), 400, false, new Dictionary<Guid, int> { [editor] = 400, [user] = 0 });

        Assert.True(EducationApiCommonService.CanEditCourse(access, course));
    }

    [Fact]
    public void Editor_CannotEditAnotherEditorCourse()
    {
        var editor = Guid.NewGuid();
        var otherEditor = Guid.NewGuid();
        var course = CourseOwnedBy(otherEditor);
        var access = new EducationAccessContext(editor, true, false, new HashSet<Guid>(), 400, false, new Dictionary<Guid, int> { [editor] = 400, [otherEditor] = 400 });

        Assert.False(EducationApiCommonService.CanEditCourse(access, course));
    }

    [Fact]
    public void Admin_CanEditLowerRoleCourse_ButNotAnotherAdminCourse()
    {
        var admin = Guid.NewGuid();
        var editor = Guid.NewGuid();
        var otherAdmin = Guid.NewGuid();
        var access = new EducationAccessContext(admin, true, false, new HashSet<Guid>(), 800, false, new Dictionary<Guid, int>
        {
            [admin] = 800,
            [editor] = 400,
            [otherAdmin] = 800
        });

        Assert.True(EducationApiCommonService.CanEditCourse(access, CourseOwnedBy(editor)));
        Assert.False(EducationApiCommonService.CanEditCourse(access, CourseOwnedBy(otherAdmin)));
    }

    [Fact]
    public void Admin_CanAlwaysEditOwnCourse_EvenAtEqualRank()
    {
        var admin = Guid.NewGuid();
        var access = new EducationAccessContext(admin, true, false, new HashSet<Guid>(), 800, false, new Dictionary<Guid, int> { [admin] = 800 });

        Assert.True(EducationApiCommonService.CanEditCourse(access, CourseOwnedBy(admin)));
    }

    [Fact]
    public void SuperAdmin_CanEditAnyCourse()
    {
        var super = Guid.NewGuid();
        var otherSuper = Guid.NewGuid();
        var access = new EducationAccessContext(super, true, false, new HashSet<Guid>(), 1000, true, new Dictionary<Guid, int> { [super] = 1000, [otherSuper] = 1000 });

        Assert.True(EducationApiCommonService.CanEditCourse(access, CourseOwnedBy(otherSuper)));
    }

    [Fact]
    public void StudentVisibilityBypass_DoesNotGrantCourseEditPermission()
    {
        var course = CourseOwnedBy();
        course.IsPublic = false;
        course.IsHiddenFromStudents = true;
        var access = new EducationAccessContext(Guid.NewGuid(), false, true, new HashSet<Guid>());

        Assert.True(EducationApiCommonService.CanViewCourse(access, course));
        Assert.False(EducationApiCommonService.CanEditCourse(access, course));
    }
}
