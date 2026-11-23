using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Noor.Api.Tests;

public class CompanyAccessTests(ApiFixture f) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => CancellationToken.None;

    private async Task<Guid> Task(string name, Guid? parent = null)
    {
        var r = await f.As(f.Pm).PostAsJsonAsync($"/api/projects/{f.Project}/tasks", new { name, parentId = parent }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<HashSet<Guid>> Visible(Guid user)
    {
        var list = await (await f.As(user).GetAsync($"/api/projects/{f.Project}/tasks", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        return list.EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToHashSet();
    }

    private Task<HttpResponseMessage> Assign(Guid actor, Guid? organization, params Guid[] tasks) =>
        f.As(actor).PostAsJsonAsync($"/api/projects/{f.Project}/tasks/assign-organization", new { taskIds = tasks, organizationId = organization }, Ct);

    [Fact]
    public async Task Unassigned_task_is_visible_to_managers_only()
    {
        var id = await Task("unassigned");
        Assert.Contains(id, await Visible(f.Pm));
        Assert.DoesNotContain(id, await Visible(f.ExecA));
        Assert.DoesNotContain(id, await Visible(f.Viewer));
    }

    [Fact]
    public async Task Branch_can_be_assigned_later_and_returned()
    {
        var parent = await Task("branch");
        var child = await Task("branch child", parent);
        var grandchild = await Task("branch grandchild", child);

        var assigned = await Assign(f.Pm, f.OrgB, parent);
        Assert.Equal(3, (await assigned.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("changed").GetInt32());
        var visible = await Visible(f.ExecB);
        Assert.Contains(parent, visible);
        Assert.Contains(child, visible);
        Assert.Contains(grandchild, visible);
        Assert.DoesNotContain(child, await Visible(f.ExecA));

        Assert.True((await Assign(f.Pm, null, parent)).IsSuccessStatusCode);
        Assert.DoesNotContain(child, await Visible(f.ExecB));
    }

    [Fact]
    public async Task Company_cannot_assign_tasks()
    {
        var id = await Task("protected");
        Assert.Equal(HttpStatusCode.Forbidden, (await Assign(f.ExecA, f.OrgA, id)).StatusCode);
    }

    [Fact]
    public async Task Unlinked_company_is_rejected()
    {
        var id = await Task("wrong company");
        Assert.Equal(HttpStatusCode.BadRequest, (await Assign(f.Pm, Guid.NewGuid(), id)).StatusCode);
    }

    [Fact]
    public async Task Company_sees_parent_heading_without_parent_details()
    {
        var parent = await Task("private heading");
        var child = await Task("shared child", parent);
        await using (var c = new NpgsqlConnection(ApiFixture.Cs))
        {
            await c.OpenAsync(Ct);
            await using var q = new NpgsqlCommand($"UPDATE tasks SET notes='secret note' WHERE id='{parent}'", c);
            await q.ExecuteNonQueryAsync(Ct);
        }
        Assert.True((await Assign(f.Pm, f.OrgA, child)).IsSuccessStatusCode);

        var text = await (await f.As(f.ExecA).GetAsync($"/api/projects/{f.Project}/task-context", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("private heading", text);
        Assert.DoesNotContain("secret note", text);
        Assert.DoesNotContain(parent, await Visible(f.ExecA));
    }

    [Fact]
    public async Task Manager_can_add_company_without_knowing_task_mapping()
    {
        var r = await f.As(f.Pm).PostAsJsonAsync($"/api/projects/{f.Project}/organizations", new { name = "شرکت تست " + Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var denied = await f.As(f.ExecA).PostAsJsonAsync($"/api/projects/{f.Project}/organizations", new { name = "x" + Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Executor_requires_a_company()
    {
        var r = await f.As(f.Pm).PutAsJsonAsync($"/api/projects/{f.Project}/members/{f.Viewer}", new { role = "executor", organizationId = (Guid?)null }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Project_manager_cannot_grant_system_admin()
    {
        var r = await f.As(f.Pm).PutAsJsonAsync($"/api/projects/{f.Project}/members/{f.Viewer}", new { role = "system_admin", organizationId = (Guid?)null }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Last_manager_cannot_be_removed()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await f.As(f.Pm).DeleteAsync($"/api/projects/{f.Project}/members/{f.Pm}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Revoked_member_loses_access_immediately()
    {
        var user = Guid.NewGuid();
        await using (var c = new NpgsqlConnection(ApiFixture.Cs))
        {
            await c.OpenAsync(Ct);
            await using var q = new NpgsqlCommand($"INSERT INTO users(id,identity_subject,display_name) VALUES ('{user}','{user}','temp')", c);
            await q.ExecuteNonQueryAsync(Ct);
        }
        var url = $"/api/projects/{f.Project}/members/{user}";
        Assert.Equal(HttpStatusCode.NoContent, (await f.As(f.Pm).PutAsJsonAsync(url, new { role = "viewer", organizationId = f.OrgA }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.As(user).GetAsync($"/api/projects/{f.Project}/tasks", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await f.As(f.Pm).DeleteAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.As(user).GetAsync($"/api/projects/{f.Project}/tasks", Ct)).StatusCode);
    }
}
