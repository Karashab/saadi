using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Noor.Api;
using Npgsql;

namespace Noor.Api.Tests;

public sealed class ApiFixture : WebApplicationFactory<Program>
{
    public const string Key = "noor-integration-test-signing-key-32chars";
    public static string Cs => Environment.GetEnvironmentVariable("NOOR_TEST_DB")
        ?? throw new InvalidOperationException("NOOR_TEST_DB is required");

    public Guid Project = Guid.NewGuid(), OrgA = Guid.NewGuid(), OrgB = Guid.NewGuid();
    public Guid ExecA = Guid.NewGuid(), ExecB = Guid.NewGuid(), Pm = Guid.NewGuid(), Viewer = Guid.NewGuid();
    public Guid TaskA = Guid.NewGuid(), TaskB = Guid.NewGuid(), Overdue = Guid.NewGuid(), Undated = Guid.NewGuid();
    public const string ExactTitle = "ها Firewall  خرید -تست";

    public ApiFixture()
    {
        using var c = new NpgsqlConnection(Cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO organizations(id,name) VALUES ('{OrgA}','a-{OrgA}'),('{OrgB}','b-{OrgB}');
            INSERT INTO users(id,identity_subject,display_name) VALUES
              ('{ExecA}','{ExecA}','a'),('{ExecB}','{ExecB}','b'),('{Pm}','{Pm}','pm'),('{Viewer}','{Viewer}','v');
            INSERT INTO projects(id,name) VALUES ('{Project}','api test');
            INSERT INTO project_members VALUES
              ('{Project}','{ExecA}','{OrgA}','executor'),('{Project}','{ExecB}','{OrgB}','executor'),
              ('{Project}','{Pm}','{OrgA}','project_manager'),('{Project}','{Viewer}','{OrgA}','viewer');
            INSERT INTO project_organizations VALUES ('{Project}','{OrgA}'),('{Project}','{OrgB}');
            INSERT INTO tasks(id,project_id,organization_id,legacy_id,sort_order,wbs,name,percent_complete,finish_date,assignee_user_id) VALUES
              ('{TaskA}','{Project}','{OrgA}','a',0,'1','{ExactTitle}',0,NULL,NULL),
              ('{TaskB}','{Project}','{OrgB}','b',1,'2','task b',0,NULL,NULL),
              ('{Overdue}','{Project}','{OrgA}','o',2,'3','late',10,'2026-11-20','{ExecA}'),
              ('{Undated}','{Project}','{OrgA}','u',3,'4','undated',0,NULL,'{ExecA}');
            """;
        cmd.ExecuteNonQuery();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Noor", Environment.GetEnvironmentVariable("NOOR_TEST_API_DB") ?? Cs);
        builder.UseSetting("Auth:TestSigningKey", Key);
        builder.UseSetting("Worker:Enabled", "false");
    }

    public HttpClient As(Guid user)
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "noor-test", Audience = "noor-api", Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity([new Claim("sub", user.ToString())]),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256),
        });
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

public class ApiTests(ApiFixture f) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => CancellationToken.None;

    private async Task<int> Version(Guid task)
    {
        var list = await (await f.As(f.Pm).GetAsync($"/api/projects/{f.Project}/tasks", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        return list.EnumerateArray().First(t => t.GetProperty("id").GetGuid() == task).GetProperty("version").GetInt32();
    }

    [Fact]
    public async Task Missing_token_is_rejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/projects", Ct)).StatusCode);

    [Fact]
    public async Task Executor_only_lists_own_company_tasks()
    {
        var list = await (await f.As(f.ExecB).GetAsync($"/api/projects/{f.Project}/tasks", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(f.TaskB, Assert.Single(list.EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Other_company_task_is_not_found()
    {
        var r = await f.As(f.ExecB).PatchAsJsonAsync($"/api/tasks/{f.TaskA}", new { version = 1, percentComplete = 50 }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Viewer_cannot_update()
    {
        var r = await f.As(f.Viewer).PatchAsJsonAsync($"/api/tasks/{f.Undated}", new { version = await Version(f.Undated), notes = "x" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Executor_cannot_rename_task()
    {
        var r = await f.As(f.ExecA).PatchAsJsonAsync($"/api/tasks/{f.TaskA}", new { version = await Version(f.TaskA), name = "changed" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Update_is_versioned_and_audited()
    {
        var created = await f.As(f.Pm).PostAsJsonAsync($"/api/projects/{f.Project}/tasks", new { name = "versioned", organizationId = f.OrgA }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        Assert.True((await f.As(f.ExecA).PatchAsJsonAsync($"/api/tasks/{id}", new { version = 1, percentComplete = 40 }, Ct)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.As(f.Pm).PatchAsJsonAsync($"/api/tasks/{id}", new { version = 1, percentComplete = 90 }, Ct)).StatusCode);

        await using var c = new NpgsqlConnection(ApiFixture.Cs);
        await c.OpenAsync(Ct);
        await using var q = new NpgsqlCommand($"SELECT count(*) FROM audit_log WHERE entity_id='{id}' AND action='update' AND actor_user_id='{f.ExecA}'", c);
        Assert.Equal(1L, await q.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Invalid_dates_are_rejected()
    {
        var r = await f.As(f.Pm).PostAsJsonAsync($"/api/projects/{f.Project}/tasks",
            new { name = "bad", startDate = "2026-12-02", finishDate = "2026-12-01" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Delete_is_soft_and_hidden()
    {
        var created = await f.As(f.Pm).PostAsJsonAsync($"/api/projects/{f.Project}/tasks", new { name = "to delete", organizationId = f.OrgA }, Ct);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As(f.ExecA).DeleteAsync($"/api/tasks/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await f.As(f.Pm).DeleteAsync($"/api/tasks/{id}", Ct)).StatusCode);

        await using var c = new NpgsqlConnection(ApiFixture.Cs);
        await c.OpenAsync(Ct);
        await using var q = new NpgsqlCommand($"SELECT count(*) FROM tasks WHERE id='{id}' AND deleted_at IS NOT NULL", c);
        Assert.Equal(1L, await q.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Deadline_job_alerts_escalates_and_does_not_repeat()
    {
        var job = f.Services.GetRequiredService<DeadlineJob>();
        var today = new DateOnly(2026, 11, 23); // three days after the seeded finish date
        await job.RunAsync(today, Ct);
        await job.RunAsync(today, Ct);

        foreach (var user in new[] { f.ExecA, f.Pm })
        {
            var inbox = await (await f.As(user).GetAsync("/api/notifications", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
            var mine = inbox.EnumerateArray().Where(n => n.GetProperty("task_id").ValueKind == JsonValueKind.String
                && n.GetProperty("task_id").GetGuid() == f.Overdue).ToList();
            Assert.Equal("overdue", Assert.Single(mine).GetProperty("kind").GetString());
            Assert.DoesNotContain(inbox.EnumerateArray(), n => n.GetProperty("task_id").ValueKind == JsonValueKind.String
                && n.GetProperty("task_id").GetGuid() == f.Undated);
        }
        var other = await (await f.As(f.ExecB).GetAsync("/api/notifications", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Empty(other.EnumerateArray());
    }

    [Fact]
    public async Task Excel_report_keeps_title_exact()
    {
        var r = await f.As(f.Pm).GetAsync($"/api/projects/{f.Project}/reports/tasks.xlsx", Ct);
        r.EnsureSuccessStatusCode();
        using var book = new XLWorkbook(await r.Content.ReadAsStreamAsync(Ct));
        Assert.Contains(book.Worksheet(1).RowsUsed(), row => row.Cell(2).GetString() == ApiFixture.ExactTitle);
    }
}
