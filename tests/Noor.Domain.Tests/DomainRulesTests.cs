using Noor.Domain;

namespace Noor.Domain.Tests;

public class AccessPolicyTests
{
    private static readonly Guid Project = Guid.NewGuid(), OrgA = Guid.NewGuid(), OrgB = Guid.NewGuid();
    private static Membership Member(MemberRole role, Guid? org = null) => new(Guid.NewGuid(), Project, org ?? OrgA, role);

    [Fact]
    public void Executor_cannot_touch_another_company() =>
        Assert.False(AccessPolicy.Can(Member(MemberRole.Executor), Permission.ViewTask, new(Project, OrgB)));

    [Fact]
    public void Executor_can_update_but_not_delete_own_company_task()
    {
        var member = Member(MemberRole.Executor);
        Assert.True(AccessPolicy.Can(member, Permission.UpdateTask, new(Project, OrgA)));
        Assert.False(AccessPolicy.Can(member, Permission.DeleteTask, new(Project, OrgA)));
    }

    [Fact]
    public void Viewer_is_read_only()
    {
        var member = Member(MemberRole.Viewer);
        Assert.True(AccessPolicy.Can(member, Permission.ViewTask, new(Project, OrgA)));
        Assert.False(AccessPolicy.Can(member, Permission.UpdateTask, new(Project, OrgA)));
        Assert.False(AccessPolicy.Can(member, Permission.UploadDocument, new(Project, OrgA)));
    }

    [Fact]
    public void Project_manager_sees_all_companies_and_unassigned_tasks()
    {
        var member = Member(MemberRole.ProjectManager);
        Assert.True(AccessPolicy.Can(member, Permission.UpdateTask, new(Project, OrgB)));
        Assert.True(AccessPolicy.Can(member, Permission.ViewTask, new(Project, null)));
    }

    [Fact]
    public void Company_admin_cannot_see_unassigned_task() =>
        Assert.False(AccessPolicy.Can(Member(MemberRole.CompanyAdmin), Permission.ViewTask, new(Project, null)));

    [Fact]
    public void Membership_in_another_project_grants_nothing()
    {
        var member = new Membership(Guid.NewGuid(), Guid.NewGuid(), OrgA, MemberRole.SystemAdmin);
        Assert.False(AccessPolicy.Can(member, Permission.ViewTask, new(Project, OrgA)));
    }

    [Fact]
    public void No_membership_grants_nothing() =>
        Assert.False(AccessPolicy.Can(null, Permission.ViewTask, new(Project, OrgA)));
}

public class DeadlineAlertTests
{
    private static readonly DateOnly Today = new(2026, 11, 23);
    private static TaskDeadline Task(int finishOffset, decimal pct = 0, bool summary = false) =>
        new(Guid.NewGuid(), Today.AddDays(finishOffset), pct, summary);

    [Theory]
    [InlineData(3, AlertKind.DueSoon)]
    [InlineData(1, AlertKind.DueSoon)]
    [InlineData(0, AlertKind.DueToday)]
    [InlineData(-1, AlertKind.Overdue)]
    [InlineData(-3, AlertKind.Overdue)]
    [InlineData(-7, AlertKind.Overdue)]
    [InlineData(-14, AlertKind.Overdue)]
    public void Fires_on_schedule(int offset, AlertKind kind) =>
        Assert.Equal(kind, DeadlineAlerts.Evaluate(Task(offset), Today)?.Kind);

    [Theory]
    [InlineData(5)]
    [InlineData(2)]
    [InlineData(-2)]
    [InlineData(-4)]
    [InlineData(-8)]
    public void Stays_quiet_between_reminders(int offset) =>
        Assert.Null(DeadlineAlerts.Evaluate(Task(offset), Today));

    [Fact]
    public void One_day_overdue_goes_to_assignee_only() =>
        Assert.Equal(AlertAudience.Assignee, DeadlineAlerts.Evaluate(Task(-1), Today)!.Audience);

    [Fact]
    public void Three_days_overdue_escalates_to_manager() =>
        Assert.Equal(AlertAudience.AssigneeAndManager, DeadlineAlerts.Evaluate(Task(-3), Today)!.Audience);

    [Fact]
    public void Completed_task_never_alerts() => Assert.Null(DeadlineAlerts.Evaluate(Task(-1, 100), Today));

    [Fact]
    public void Summary_row_never_alerts() => Assert.Null(DeadlineAlerts.Evaluate(Task(-1, 0, true), Today));

    [Fact]
    public void Task_without_date_never_alerts() =>
        Assert.Null(DeadlineAlerts.Evaluate(new TaskDeadline(Guid.NewGuid(), null, 0, false), Today));

    [Fact]
    public void Dedupe_key_is_stable_for_same_reminder()
    {
        var task = Task(-1);
        Assert.Equal(DeadlineAlerts.Evaluate(task, Today)!.DedupeKey, DeadlineAlerts.Evaluate(task, Today)!.DedupeKey);
    }
}
