using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using ClosedXML.Excel;
using Noor.Domain;

namespace Noor.Api;

public static class Endpoints
{
    private const string TaskJson = "to_jsonb(t) - 'deleted_at'";
    private const string JsonType = "application/json; charset=utf-8";

    private enum FieldKind { Text, Date, Number }

    private static readonly Dictionary<string, (string Column, FieldKind Kind)> PatchFields = new()
    {
        ["name"] = ("name", FieldKind.Text), ["wbs"] = ("wbs", FieldKind.Text),
        ["startDate"] = ("start_date", FieldKind.Date), ["finishDate"] = ("finish_date", FieldKind.Date),
        ["durationDays"] = ("duration_days", FieldKind.Number), ["percentComplete"] = ("percent_complete", FieldKind.Number),
        ["priority"] = ("priority", FieldKind.Text), ["assigneeText"] = ("assignee_text", FieldKind.Text),
        ["predecessors"] = ("predecessors", FieldKind.Text), ["notes"] = ("notes", FieldKind.Text),
    };

    public static DateOnly TehranToday(TimeProvider clock) => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran")).DateTime);

    public static void MapNoor(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/projects", (ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
            Results.Text((string)(await d.Scalar(
                "SELECT coalesce(jsonb_agg(jsonb_build_object('id',id,'code',code,'name',name,'title',title) ORDER BY name),'[]')::text FROM projects",
                ct))!, JsonType), ct));

        api.MapGet("/projects/{projectId:guid}/tasks", (Guid projectId, ClaimsPrincipal user, NoorDb db, CancellationToken ct) =>
            db.Run(user, async d =>
            {
                await Require(d, projectId, null, Permission.ViewTask, ct, anyScope: true);
                return Results.Text((string)(await d.Scalar(
                    $"SELECT coalesce(jsonb_agg({TaskJson} ORDER BY t.parent_id NULLS FIRST, t.sort_order),'[]')::text FROM tasks t WHERE t.project_id=@p AND t.deleted_at IS NULL",
                    ct, ("p", projectId)))!, JsonType);
            }, ct));

        api.MapPost("/projects/{projectId:guid}/tasks", CreateTask);
        api.MapPatch("/tasks/{taskId:guid}", UpdateTask);
        api.MapDelete("/tasks/{taskId:guid}", DeleteTask);

        api.MapGet("/notifications", (ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
            Results.Text((string)(await d.Scalar(
                """
                SELECT coalesce(jsonb_agg(to_jsonb(n) - 'dedupe_key' ORDER BY n.created_at DESC),'[]')::text
                FROM (SELECT * FROM notifications WHERE user_id=@u ORDER BY created_at DESC LIMIT 200) n
                """, ct, ("u", d.UserId)))!, JsonType), ct));

        api.MapPost("/notifications/{id:guid}/read", (Guid id, ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
            await d.Execute("UPDATE notifications SET read_at=coalesce(read_at, now()) WHERE id=@i", ct, ("i", id)) == 1
                ? Results.NoContent() : throw new ApiFailure(404, "اعلان پیدا نشد."), ct));

        api.MapGet("/projects/{projectId:guid}/reports/summary", (Guid projectId, ClaimsPrincipal user, NoorDb db, TimeProvider clock, CancellationToken ct) =>
            db.Run(user, async d =>
            {
                await Require(d, projectId, null, Permission.ViewReports, ct, anyScope: true);
                return Results.Text((string)(await d.Scalar(
                    """
                    SELECT jsonb_build_object(
                      'tasks', count(*),
                      'notStarted', count(*) FILTER (WHERE coalesce(percent_complete,0)=0),
                      'inProgress', count(*) FILTER (WHERE percent_complete>0 AND percent_complete<100),
                      'completed', count(*) FILTER (WHERE percent_complete>=100),
                      'overdue', count(*) FILTER (WHERE finish_date<@today AND coalesce(percent_complete,0)<100),
                      'withoutDates', count(*) FILTER (WHERE finish_date IS NULL)
                    )::text FROM tasks WHERE project_id=@p AND deleted_at IS NULL AND NOT is_summary
                    """, ct, ("p", projectId), ("today", TehranToday(clock))))!, JsonType);
            }, ct));

        api.MapGet("/projects/{projectId:guid}/reports/tasks.xlsx", ExportTasks);
    }

    private static async Task<Membership> Require(RequestDb db, Guid projectId, Guid? organizationId, Permission permission,
        CancellationToken ct, bool anyScope = false)
    {
        var member = await db.Member(projectId, ct) ?? throw new ApiFailure(404, "پروژه پیدا نشد.");
        var scope = new ResourceScope(projectId, anyScope ? member.OrganizationId : organizationId);
        if (!AccessPolicy.Can(member, permission, scope)) throw new ApiFailure(403, "دسترسی لازم را ندارید.");
        return member;
    }

    private sealed record TaskState(Guid ProjectId, Guid? OrganizationId, bool IsSummary, int Version, Guid? ParentId,
        DateOnly? Start, DateOnly? Finish, string Before);

    private static async Task<TaskState> LoadTask(RequestDb db, Guid id, CancellationToken ct)
    {
        await using var command = db.Command(
            $"""
            SELECT t.project_id, t.organization_id, t.is_summary, t.version, t.parent_id, t.start_date, t.finish_date, ({TaskJson})::text
            FROM tasks t WHERE t.id=@i AND t.deleted_at IS NULL
            """, ("i", id));
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new ApiFailure(404, "تسک پیدا نشد.");
        return new TaskState(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetBoolean(2), r.GetInt32(3),
            r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateOnly>(5),
            r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6), r.GetString(7));
    }

    /// <summary>Locks a task after the permission check; row locks require write access under RLS.</summary>
    private static async Task LockTask(RequestDb db, Guid id, int expectedVersion, CancellationToken ct)
    {
        var current = await db.Scalar("SELECT version FROM tasks WHERE id=@i AND deleted_at IS NULL FOR UPDATE", ct, ("i", id));
        if (current is not int version) throw new ApiFailure(403, "دسترسی لازم را ندارید.");
        if (version != expectedVersion) throw new ApiFailure(409, "این تسک را کاربر دیگری تغییر داده است. صفحه را تازه کنید.");
    }

    public sealed record CreateTaskRequest(string Name, Guid? ParentId, Guid? OrganizationId, string? Wbs,
        DateOnly? StartDate, DateOnly? FinishDate, decimal? DurationDays, string? AssigneeText, string? Notes);

    private static Task<IResult> CreateTask(Guid projectId, CreateTaskRequest body, HttpContext http, NoorDb db, CancellationToken ct) =>
        db.Run(http.User, async d =>
        {
            if (string.IsNullOrWhiteSpace(body.Name)) throw new ApiFailure(400, "عنوان تسک لازم است.");
            ValidateDates(body.StartDate, body.FinishDate);
            var member = await d.Member(projectId, ct) ?? throw new ApiFailure(404, "پروژه پیدا نشد.");
            var organizationId = AccessPolicy.HasProjectWideScope(member.Role) ? body.OrganizationId : member.OrganizationId;
            await Require(d, projectId, organizationId, Permission.CreateTask, ct);

            if (body.ParentId is { } parentId)
            {
                var parent = await LoadTask(d, parentId, ct);
                if (parent.ProjectId != projectId) throw new ApiFailure(400, "تسک والد در این پروژه نیست.");
                await d.Execute("UPDATE tasks SET is_summary=true, percent_complete=NULL, version=version+1, updated_at=now() WHERE id=@i AND NOT is_summary",
                    ct, ("i", parentId));
            }

            var id = Guid.NewGuid();
            var after = (string)(await d.Scalar(
                $"""
                INSERT INTO tasks AS t (id, project_id, parent_id, organization_id, legacy_id, sort_order, name, wbs,
                  start_date, finish_date, duration_days, percent_complete, assignee_text, notes)
                VALUES (@i, @p, @parent, @org, @legacy,
                  (SELECT coalesce(max(sort_order),-1)+1 FROM tasks WHERE project_id=@p AND parent_id IS NOT DISTINCT FROM @parent::uuid),
                  @name, @wbs, @start::date, @finish::date, @dur::numeric, 0, @assignee, @notes)
                RETURNING ({TaskJson})::text
                """, ct, ("i", id), ("p", projectId), ("parent", body.ParentId), ("org", organizationId),
                ("legacy", "new:" + id.ToString("N")), ("name", body.Name), ("wbs", body.Wbs), ("start", body.StartDate),
                ("finish", body.FinishDate), ("dur", body.DurationDays), ("assignee", body.AssigneeText), ("notes", body.Notes)))!;
            await d.Audit(projectId, "task", id.ToString(), "create", null, after, http.Connection.RemoteIpAddress, ct);
            return Results.Text(after, JsonType, statusCode: 201);
        }, ct);

    private static Task<IResult> UpdateTask(Guid taskId, JsonElement body, HttpContext http, NoorDb db, CancellationToken ct) =>
        db.Run(http.User, async d =>
        {
            if (body.ValueKind != JsonValueKind.Object) throw new ApiFailure(400, "بدنه درخواست معتبر نیست.");
            if (!body.TryGetProperty("version", out var versionValue) || !versionValue.TryGetInt32(out var version))
                throw new ApiFailure(400, "نسخه تسک لازم است.");

            var task = await LoadTask(d, taskId, ct);
            var member = await Require(d, task.ProjectId, task.OrganizationId, Permission.UpdateTask, ct);
            if (task.Version != version) throw new ApiFailure(409, "این تسک را کاربر دیگری تغییر داده است. صفحه را تازه کنید.");
            await LockTask(d, taskId, version, ct);

            var sets = new List<string>();
            var args = new List<(string, object?)> { ("id", taskId), ("v", version) };
            DateOnly? start = task.Start, finish = task.Finish;
            foreach (var property in body.EnumerateObject())
            {
                if (property.Name == "version") continue;
                if (!PatchFields.TryGetValue(property.Name, out var field)) throw new ApiFailure(400, $"فیلد نامعتبر: {property.Name}");
                if (property.Name is "name" or "wbs" && member.Role == MemberRole.Executor)
                    throw new ApiFailure(403, "تغییر عنوان و کد WBS برای این نقش مجاز نیست.");
                if (property.Name == "percentComplete" && task.IsSummary)
                    throw new ApiFailure(400, "درصد سرگروه از زیرمجموعه‌ها محاسبه می‌شود.");

                var value = ReadValue(property, field.Kind);
                if (property.Name == "name" && string.IsNullOrWhiteSpace(value as string)) throw new ApiFailure(400, "عنوان تسک لازم است.");
                if (property.Name == "percentComplete" && value is decimal pct && pct is < 0 or > 100) throw new ApiFailure(400, "درصد باید بین ۰ تا ۱۰۰ باشد.");
                if (property.Name == "percentComplete" && value is null) throw new ApiFailure(400, "درصد لازم است.");
                if (property.Name == "startDate") start = (DateOnly?)value;
                if (property.Name == "finishDate") finish = (DateOnly?)value;

                var parameter = "f" + sets.Count;
                var cast = field.Kind switch { FieldKind.Date => "::date", FieldKind.Number => "::numeric", _ => "::text" };
                sets.Add($"{field.Column}=@{parameter}{cast}");
                args.Add((parameter, value));
            }
            if (sets.Count == 0) throw new ApiFailure(400, "تغییری ارسال نشده است.");
            ValidateDates(start, finish);

            var after = await d.Scalar(
                $"UPDATE tasks t SET {string.Join(',', sets)}, version=version+1, updated_at=now() WHERE id=@id AND version=@v RETURNING ({TaskJson})::text",
                ct, [.. args]) as string ?? throw new ApiFailure(403, "دسترسی لازم را ندارید.");
            await d.Audit(task.ProjectId, "task", taskId.ToString(), "update", task.Before, after, http.Connection.RemoteIpAddress, ct);
            return Results.Text(after, JsonType);
        }, ct);

    private static Task<IResult> DeleteTask(Guid taskId, HttpContext http, NoorDb db, CancellationToken ct) =>
        db.Run(http.User, async d =>
        {
            var task = await LoadTask(d, taskId, ct);
            await Require(d, task.ProjectId, task.OrganizationId, Permission.DeleteTask, ct);
            await LockTask(d, taskId, task.Version, ct);
            if (await d.Scalar("SELECT 1 FROM tasks WHERE parent_id=@i AND deleted_at IS NULL LIMIT 1", ct, ("i", taskId)) is not null)
                throw new ApiFailure(409, "ابتدا زیرمجموعه‌های این ردیف را حذف کنید.");
            if (await d.Execute("UPDATE tasks SET deleted_at=now(), version=version+1, updated_at=now() WHERE id=@i", ct, ("i", taskId)) != 1)
                throw new ApiFailure(403, "دسترسی لازم را ندارید.");
            await d.Audit(task.ProjectId, "task", taskId.ToString(), "delete", task.Before, null, http.Connection.RemoteIpAddress, ct);
            return Results.NoContent();
        }, ct);

    private static Task<IResult> ExportTasks(Guid projectId, ClaimsPrincipal user, NoorDb db, CancellationToken ct) =>
        db.Run(user, async d =>
        {
            await Require(d, projectId, null, Permission.ViewReports, ct, anyScope: true);
            using var workbook = new XLWorkbook();
            var sheet = workbook.AddWorksheet("Tasks");
            sheet.RightToLeft = true;
            string[] headers = ["WBS", "عنوان", "شروع", "پایان", "مدت", "درصد", "مسئول"];
            for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).SetValue(headers[c]);

            await using var command = d.Command(
                """
                SELECT wbs, name, coalesce(legacy_start, start_date::text), coalesce(legacy_finish, finish_date::text),
                       duration_days, percent_complete, assignee_text
                FROM tasks WHERE project_id=@p AND deleted_at IS NULL ORDER BY string_to_array(wbs,'.')::text[], sort_order
                """, ("p", projectId));
            await using var r = await command.ExecuteReaderAsync(ct);
            for (var row = 2; await r.ReadAsync(ct); row++)
            {
                foreach (var c in new[] { 0, 1, 2, 3, 6 }) if (!r.IsDBNull(c)) sheet.Cell(row, c + 1).SetValue(r.GetString(c));
                foreach (var c in new[] { 4, 5 }) if (!r.IsDBNull(c)) sheet.Cell(row, c + 1).SetValue(r.GetDecimal(c));
            }
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return Results.File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "noor-tasks.xlsx");
        }, ct);

    private static object? ReadValue(JsonProperty property, FieldKind kind)
    {
        var value = property.Value;
        if (value.ValueKind == JsonValueKind.Null) return null;
        switch (kind)
        {
            case FieldKind.Text when value.ValueKind == JsonValueKind.String:
                return value.GetString();
            case FieldKind.Number when value.TryGetDecimal(out var number):
                return number;
            case FieldKind.Date when value.ValueKind == JsonValueKind.String &&
                DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                return date;
            default:
                throw new ApiFailure(400, $"مقدار نامعتبر برای {property.Name}");
        }
    }

    private static void ValidateDates(DateOnly? start, DateOnly? finish)
    {
        if (start is not null && finish is not null && finish < start)
            throw new ApiFailure(400, "تاریخ پایان نباید قبل از شروع باشد.");
    }
}
