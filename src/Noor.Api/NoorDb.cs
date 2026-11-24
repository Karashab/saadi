using System.Security.Claims;
using Noor.Domain;
using Npgsql;

namespace Noor.Api;

public sealed class ApiFailure(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class RequestDb(NpgsqlConnection connection, NpgsqlTransaction transaction)
{
    public Guid UserId { get; set; }

    public NpgsqlCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public async Task<object?> Scalar(string sql, CancellationToken ct, params (string, object?)[] args)
    {
        await using var command = Command(sql, args);
        var value = await command.ExecuteScalarAsync(ct);
        return value is DBNull ? null : value;
    }

    public async Task<int> Execute(string sql, CancellationToken ct, params (string, object?)[] args)
    {
        await using var command = Command(sql, args);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<Membership?> Member(Guid projectId, CancellationToken ct)
    {
        await using var command = Command(
            "SELECT organization_id, role::text FROM project_members WHERE project_id=@p AND user_id=@u",
            ("p", projectId), ("u", UserId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new Membership(UserId, projectId, reader.IsDBNull(0) ? null : reader.GetGuid(0), ParseRole(reader.GetString(1)));
    }

    public Task Audit(Guid projectId, string entity, string id, string action, string? before, string? after,
        System.Net.IPAddress? ip, CancellationToken ct) => Execute(
        """
        INSERT INTO audit_log (project_id, actor_user_id, entity_type, entity_id, action, before_data, after_data, ip)
        VALUES (@p, @u, @e, @i, @a, @b::jsonb, @n::jsonb, @ip)
        """, ct, ("p", projectId), ("u", UserId), ("e", entity), ("i", id), ("a", action),
        ("b", (object?)before ?? DBNull.Value), ("n", (object?)after ?? DBNull.Value), ("ip", ip));

    private static MemberRole ParseRole(string role) => role switch
    {
        "system_admin" => MemberRole.SystemAdmin,
        "company_admin" => MemberRole.CompanyAdmin,
        "project_manager" => MemberRole.ProjectManager,
        "executor" => MemberRole.Executor,
        "viewer" => MemberRole.Viewer,
        _ => throw new InvalidOperationException("unknown role"),
    };
}

public sealed class NoorDb(NpgsqlDataSource source)
{
    /// <summary>Runs one request in a transaction as the restricted role, so database policies always apply.</summary>
    public async Task<IResult> Run(ClaimsPrincipal principal, Func<RequestDb, Task<IResult>> action, CancellationToken ct)
    {
        var subject = principal.FindFirstValue("sub");
        if (string.IsNullOrEmpty(subject)) return Results.Unauthorized();

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var db = new RequestDb(connection, transaction);
        await db.Execute("SET LOCAL ROLE noor_app", ct);
        await db.Execute("SELECT set_config('app.user_id', '', true)", ct);

        if (await db.Scalar("SELECT id FROM users WHERE identity_subject=@s", ct, ("s", subject)) is null)
            await db.Scalar("SELECT ensure_user(@s, @n, @u, @e)", ct, ("s", subject), ("n", principal.FindFirstValue("name")),
                ("u", principal.FindFirstValue("preferred_username")), ("e", principal.FindFirstValue("email")));
        if (await db.Scalar("SELECT id FROM users WHERE identity_subject=@s AND is_active", ct, ("s", subject)) is not Guid userId)
            return Error(403, "این کاربر در سامانه فعال نیست.");
        db.UserId = userId;
        await db.Execute("SELECT set_config('app.user_id', @u, true)", ct, ("u", userId.ToString()));

        try
        {
            var result = await action(db);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (ApiFailure failure)
        {
            await transaction.RollbackAsync(ct);
            return Error(failure.Status, failure.Message);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            await transaction.RollbackAsync(ct);
            return Error(403, "دسترسی لازم را ندارید.");
        }
    }

    public static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
