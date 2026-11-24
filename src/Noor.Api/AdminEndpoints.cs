using System.Security.Claims;
using Noor.Domain;

namespace Noor.Api;

public static class AdminEndpoints
{
    private const string JsonType = "application/json; charset=utf-8";
    private static readonly HashSet<string> Roles = ["system_admin", "company_admin", "project_manager", "executor", "viewer"];
    private static readonly HashSet<string> WideRoles = ["system_admin", "project_manager"];

    public sealed record OrganizationRequest(string Name);
    public sealed record MemberRequest(string Role, Guid? OrganizationId);
    public sealed record AssignRequest(Guid[] TaskIds, Guid? OrganizationId);

    public static void MapNoorAdmin(this RouteGroupBuilder api)
    {
        var project = api.MapGroup("/projects/{projectId:guid}");

        project.MapGet("/me", (Guid projectId, ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
            Json(await d.Scalar(
                """
                SELECT jsonb_build_object('userId',u.id,'name',u.display_name,'role',m.role,'organizationId',m.organization_id)::text
                FROM project_members m JOIN users u ON u.id=m.user_id WHERE m.project_id=@p AND m.user_id=@u
                """, ct, ("p", projectId), ("u", d.UserId)) ?? throw new ApiFailure(404, "پروژه پیدا نشد.")), ct));

        // Directory search for managers adding people; returns no access information.
        project.MapGet("/user-directory", (Guid projectId, string? q, ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
        {
            var member = await d.Member(projectId, ct) ?? throw new ApiFailure(404, "پروژه پیدا نشد.");
            if (!AccessPolicy.Can(member, Permission.ManageMembers, new(projectId, member.OrganizationId)))
                throw new ApiFailure(403, "دسترسی لازم را ندارید.");
            var term = q?.Trim();
            if (string.IsNullOrEmpty(term) || term.Length < 2) return Json("[]");
            return Json(await d.Scalar(
                """
                SELECT coalesce(jsonb_agg(jsonb_build_object('id',u.id,'name',u.display_name,'username',u.username,'email',u.email) ORDER BY u.display_name),'[]')::text
                FROM (SELECT * FROM users WHERE is_active AND (position(lower(@q) IN lower(display_name))>0 OR position(lower(@q) IN lower(coalesce(username,'')))>0
                      OR position(lower(@q) IN lower(coalesce(email,'')))>0) ORDER BY display_name LIMIT 20) u
                """, ct, ("q", term)));
        }, ct));

        project.MapGet("/organizations", (Guid projectId, ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
        {
            _ = await d.Member(projectId, ct) ?? throw new ApiFailure(404, "پروژه پیدا نشد.");
            return Json(await d.Scalar(
                """
                SELECT coalesce(jsonb_agg(jsonb_build_object('id',o.id,'name',o.name) ORDER BY o.name),'[]')::text
                FROM project_organizations po JOIN organizations o ON o.id=po.organization_id WHERE po.project_id=@p
                """, ct, ("p", projectId)));
        }, ct));

        project.MapPost("/organizations", (Guid projectId, OrganizationRequest body, ClaimsPrincipal user, NoorDb db, HttpContext http, CancellationToken ct) => db.Run(user, async d =>
        {
            await RequireManager(d, projectId, ct);
            if (string.IsNullOrWhiteSpace(body.Name)) throw new ApiFailure(400, "نام شرکت لازم است.");
            var id = (Guid)(await d.Scalar("SELECT create_project_organization(@p, @n)", ct, ("p", projectId), ("n", body.Name)))!;
            await d.Audit(projectId, "organization", id.ToString(), "link", null, System.Text.Json.JsonSerializer.Serialize(new { name = body.Name }), http.Connection.RemoteIpAddress, ct);
            return Results.Json(new { id, name = body.Name }, statusCode: 201);
        }, ct));

        project.MapGet("/members", (Guid projectId, ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
        {
            var member = await d.Member(projectId, ct) ?? throw new ApiFailure(404, "پروژه پیدا نشد.");
            if (!AccessPolicy.Can(member, Permission.ManageMembers, new(projectId, member.OrganizationId)))
                throw new ApiFailure(403, "دسترسی لازم را ندارید.");
            return Json(await d.Scalar(
                """
                SELECT coalesce(jsonb_agg(jsonb_build_object('userId',m.user_id,'name',u.display_name,'role',m.role,'organizationId',m.organization_id) ORDER BY u.display_name),'[]')::text
                FROM project_members m JOIN users u ON u.id=m.user_id WHERE m.project_id=@p
                """, ct, ("p", projectId)));
        }, ct));

        project.MapPut("/members/{userId:guid}", (Guid projectId, Guid userId, MemberRequest body, ClaimsPrincipal user, NoorDb db, HttpContext http, CancellationToken ct) => db.Run(user, async d =>
        {
            _ = await d.Member(projectId, ct) ?? throw new ApiFailure(404, "پروژه پیدا نشد.");
            if (!Roles.Contains(body.Role)) throw new ApiFailure(400, "نقش نامعتبر است.");
            if (!WideRoles.Contains(body.Role) && body.OrganizationId is null)
                throw new ApiFailure(400, "برای این نقش باید شرکت انتخاب شود.");
            await RequireLinkedOrganization(d, projectId, body.OrganizationId, ct);
            if (await d.Scalar("SELECT 1 FROM users WHERE id=@u AND is_active", ct, ("u", userId)) is null)
                throw new ApiFailure(404, "کاربر پیدا نشد.");

            await LockMembers(d, projectId, ct);
            var before = await CurrentRole(d, projectId, userId, ct);
            if (before is not null && WideRoles.Contains(before) && !WideRoles.Contains(body.Role))
                await RequireAnotherManager(d, projectId, userId, ct);

            var changed = before is null
                ? await d.Execute("INSERT INTO project_members (project_id,user_id,organization_id,role) VALUES (@p,@u,@o::uuid,@r::member_role)", ct,
                    ("p", projectId), ("u", userId), ("o", body.OrganizationId), ("r", body.Role))
                : await d.Execute("UPDATE project_members SET organization_id=@o::uuid, role=@r::member_role WHERE project_id=@p AND user_id=@u", ct,
                    ("p", projectId), ("u", userId), ("o", body.OrganizationId), ("r", body.Role));
            if (changed != 1) throw new ApiFailure(403, "دسترسی لازم را ندارید.");
            await d.Audit(projectId, "member", userId.ToString(), before is null ? "grant" : "change",
                before is null ? null : System.Text.Json.JsonSerializer.Serialize(new { role = before }),
                System.Text.Json.JsonSerializer.Serialize(new { role = body.Role, organizationId = body.OrganizationId }),
                http.Connection.RemoteIpAddress, ct);
            return Results.NoContent();
        }, ct));

        project.MapDelete("/members/{userId:guid}", (Guid projectId, Guid userId, ClaimsPrincipal user, NoorDb db, HttpContext http, CancellationToken ct) => db.Run(user, async d =>
        {
            _ = await d.Member(projectId, ct) ?? throw new ApiFailure(404, "پروژه پیدا نشد.");
            await LockMembers(d, projectId, ct);
            var before = await CurrentRole(d, projectId, userId, ct) ?? throw new ApiFailure(404, "عضو پیدا نشد.");
            if (WideRoles.Contains(before)) await RequireAnotherManager(d, projectId, userId, ct);
            if (await d.Execute("DELETE FROM project_members WHERE project_id=@p AND user_id=@u", ct, ("p", projectId), ("u", userId)) != 1)
                throw new ApiFailure(403, "دسترسی لازم را ندارید.");
            await d.Audit(projectId, "member", userId.ToString(), "revoke", System.Text.Json.JsonSerializer.Serialize(new { role = before }), null, http.Connection.RemoteIpAddress, ct);
            return Results.NoContent();
        }, ct));

        // Assigns tasks and all their descendants; a null company returns them to managers-only.
        project.MapPost("/tasks/assign-organization", (Guid projectId, AssignRequest body, ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
        {
            await RequireManager(d, projectId, ct);
            var ids = body.TaskIds?.Distinct().ToArray() ?? [];
            if (ids.Length == 0) throw new ApiFailure(400, "حداقل یک تسک انتخاب کنید.");
            await RequireLinkedOrganization(d, projectId, body.OrganizationId, ct);
            var found = (long)(await d.Scalar("SELECT count(*) FROM tasks WHERE project_id=@p AND id = ANY(@ids) AND deleted_at IS NULL", ct, ("p", projectId), ("ids", ids)))!;
            if (found != ids.Length) throw new ApiFailure(404, "بعضی از تسک‌ها پیدا نشدند.");

            var changed = await d.Execute(
                """
                WITH RECURSIVE sub AS (
                  SELECT id FROM tasks WHERE project_id=@p AND id = ANY(@ids) AND deleted_at IS NULL
                  UNION
                  SELECT c.id FROM tasks c JOIN sub ON c.parent_id=sub.id WHERE c.deleted_at IS NULL
                ), old AS (
                  SELECT t.id, t.organization_id FROM tasks t JOIN sub ON sub.id=t.id
                  WHERE t.organization_id IS DISTINCT FROM @o::uuid FOR UPDATE OF t
                ), upd AS (
                  UPDATE tasks t SET organization_id=@o::uuid, version=t.version+1, updated_at=now()
                  FROM old WHERE t.id=old.id RETURNING t.id, old.organization_id AS previous
                )
                INSERT INTO audit_log (project_id, actor_user_id, entity_type, entity_id, action, before_data, after_data)
                SELECT @p, @u, 'task', id::text, 'assign_organization',
                       jsonb_build_object('organization_id', previous), jsonb_build_object('organization_id', @o::uuid)
                FROM upd
                """, ct, ("p", projectId), ("ids", ids), ("o", body.OrganizationId), ("u", d.UserId));
            return Results.Json(new { changed });
        }, ct));

        project.MapGet("/task-context", (Guid projectId, ClaimsPrincipal user, NoorDb db, CancellationToken ct) => db.Run(user, async d =>
        {
            _ = await d.Member(projectId, ct) ?? throw new ApiFailure(404, "پروژه پیدا نشد.");
            return Json(await d.Scalar(
                "SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY a.sort_order),'[]')::text FROM visible_task_ancestors(@p) a",
                ct, ("p", projectId)));
        }, ct));
    }

    private static IResult Json(object? value) => Results.Text((string)value!, JsonType);

    private static async Task RequireManager(RequestDb d, Guid projectId, CancellationToken ct)
    {
        var member = await d.Member(projectId, ct) ?? throw new ApiFailure(404, "پروژه پیدا نشد.");
        if (!AccessPolicy.HasProjectWideScope(member.Role)) throw new ApiFailure(403, "فقط مدیر پروژه یا مدیر سیستم مجاز است.");
    }

    private static async Task RequireLinkedOrganization(RequestDb d, Guid projectId, Guid? organizationId, CancellationToken ct)
    {
        if (organizationId is null) return;
        if (await d.Scalar("SELECT 1 FROM project_organizations WHERE project_id=@p AND organization_id=@o", ct,
                ("p", projectId), ("o", organizationId)) is null)
            throw new ApiFailure(400, "این شرکت به پروژه وصل نیست.");
    }

    private static Task LockMembers(RequestDb d, Guid projectId, CancellationToken ct) =>
        d.Execute("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", ct, ("k", "members:" + projectId));

    private static async Task<string?> CurrentRole(RequestDb d, Guid projectId, Guid userId, CancellationToken ct) =>
        await d.Scalar("SELECT role::text FROM project_members WHERE project_id=@p AND user_id=@u", ct, ("p", projectId), ("u", userId)) as string;

    private static async Task RequireAnotherManager(RequestDb d, Guid projectId, Guid userId, CancellationToken ct)
    {
        var others = (long)(await d.Scalar(
            "SELECT count(*) FROM project_members WHERE project_id=@p AND user_id<>@u AND role IN ('system_admin','project_manager')",
            ct, ("p", projectId), ("u", userId)))!;
        if (others == 0) throw new ApiFailure(409, "پروژه باید حداقل یک مدیر داشته باشد.");
    }
}
