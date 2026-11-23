namespace Noor.Domain;

public enum AlertKind { DueSoon, DueToday, Overdue }

public enum AlertAudience { Assignee, AssigneeAndManager }

public sealed record TaskDeadline(Guid TaskId, DateOnly? FinishDate, decimal PercentComplete, bool IsSummary);

public sealed record DeadlineAlert(Guid TaskId, AlertKind Kind, int Days, AlertAudience Audience, string DedupeKey);

public static class DeadlineAlerts
{
    public const int EscalationDay = 3;

    /// <summary>
    /// Returns the alert due on <paramref name="today"/>, or null. Tasks without a real finish date
    /// never alert. Overdue alerts fire on days 1, 3 and 7, then weekly.
    /// </summary>
    public static DeadlineAlert? Evaluate(TaskDeadline task, DateOnly today)
    {
        if (task.IsSummary || task.FinishDate is not { } finish || task.PercentComplete >= 100) return null;

        var delta = finish.DayNumber - today.DayNumber;
        return delta switch
        {
            3 or 1 => Build(task.TaskId, AlertKind.DueSoon, delta, AlertAudience.Assignee),
            0 => Build(task.TaskId, AlertKind.DueToday, 0, AlertAudience.Assignee),
            < 0 when IsOverdueReminderDay(-delta) => Build(task.TaskId, AlertKind.Overdue, -delta,
                -delta >= EscalationDay ? AlertAudience.AssigneeAndManager : AlertAudience.Assignee),
            _ => null,
        };
    }

    private static bool IsOverdueReminderDay(int days) => days is 1 or 3 || (days >= 7 && days % 7 == 0);

    private static DeadlineAlert Build(Guid taskId, AlertKind kind, int days, AlertAudience audience) =>
        new(taskId, kind, days, audience, $"{taskId:N}:{kind}:{days}");
}
