using Noor.Domain;
using Npgsql;

namespace Noor.Api;

public sealed class DeadlineJob(NpgsqlDataSource source)
{
    private sealed record Row(Guid Id, Guid ProjectId, string Name, DateOnly Finish, decimal Percent, Guid? Assignee);

    /// <summary>Creates today's deadline notifications. Safe to run repeatedly.</summary>
    public async Task<int> RunAsync(DateOnly today, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE noor_worker", connection, transaction))
            await role.ExecuteNonQueryAsync(ct);

        var rows = new List<Row>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT id, project_id, name, finish_date, coalesce(percent_complete,0), assignee_user_id
            FROM tasks WHERE deleted_at IS NULL AND NOT is_summary AND finish_date IS NOT NULL
              AND coalesce(percent_complete,0) < 100
            """, connection, transaction))
        await using (var r = await command.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rows.Add(new Row(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), r.GetDecimal(4),
                    r.IsDBNull(5) ? null : r.GetGuid(5)));

        var managers = new Dictionary<Guid, List<Guid>>();
        await using (var command = new NpgsqlCommand(
            "SELECT project_id, user_id FROM project_members WHERE role IN ('system_admin','project_manager')", connection, transaction))
        await using (var r = await command.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (!managers.TryGetValue(r.GetGuid(0), out var list)) managers[r.GetGuid(0)] = list = [];
                list.Add(r.GetGuid(1));
            }

        var created = 0;
        foreach (var row in rows)
        {
            var alert = DeadlineAlerts.Evaluate(new TaskDeadline(row.Id, row.Finish, row.Percent, false), today);
            if (alert is null) continue;

            var recipients = new HashSet<Guid>();
            if (row.Assignee is { } assignee) recipients.Add(assignee);
            if (alert.Audience == AlertAudience.AssigneeAndManager && managers.TryGetValue(row.ProjectId, out var list))
                recipients.UnionWith(list);

            foreach (var user in recipients)
            {
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO notifications (user_id, project_id, task_id, kind, message, dedupe_key)
                    VALUES (@u, @p, @t, @k::notification_kind, @m, @d) ON CONFLICT (user_id, dedupe_key) DO NOTHING
                    """, connection, transaction);
                insert.Parameters.AddWithValue("u", user);
                insert.Parameters.AddWithValue("p", row.ProjectId);
                insert.Parameters.AddWithValue("t", row.Id);
                insert.Parameters.AddWithValue("k", alert.Kind switch
                {
                    AlertKind.DueSoon => "due_soon", AlertKind.DueToday => "due_today", _ => "overdue",
                });
                insert.Parameters.AddWithValue("m", Message(alert, row.Name));
                insert.Parameters.AddWithValue("d", alert.DedupeKey);
                created += await insert.ExecuteNonQueryAsync(ct);
            }
        }
        await transaction.CommitAsync(ct);
        return created;
    }

    private static string Message(DeadlineAlert alert, string name) => alert.Kind switch
    {
        AlertKind.DueSoon => $"{alert.Days} روز تا مهلت «{name}» باقی مانده است.",
        AlertKind.DueToday => $"مهلت «{name}» امروز تمام می‌شود.",
        _ => $"{alert.Days} روز از مهلت «{name}» گذشته است.",
    };
}

public sealed class DeadlineWorker(DeadlineJob job, TimeProvider clock, ILogger<DeadlineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                var created = await job.RunAsync(Endpoints.TehranToday(clock), stoppingToken);
                logger.LogInformation("Deadline check created {Count} notifications", created);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Deadline check failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
