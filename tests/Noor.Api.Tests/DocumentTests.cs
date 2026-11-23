using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Noor.Api.Tests;

public class DocumentTests(ApiFixture f) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => CancellationToken.None;
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7 test file");

    private static MultipartFormDataContent Form(byte[] bytes, string fileName, params (string Key, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        foreach (var (key, value) in fields) form.Add(new StringContent(value), key);
        return form;
    }

    private async Task<(Guid Id, Guid Version)> Upload(Guid user, Guid? organization = null, string title = "doc")
    {
        var fields = new List<(string, string)> { ("title", title) };
        if (organization is { } org) fields.Add(("organizationId", org.ToString()));
        var r = await f.As(user).PostAsync($"/api/projects/{f.Project}/documents", Form(Pdf, "plan.pdf", [.. fields]), Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (j.GetProperty("id").GetGuid(), j.GetProperty("versionId").GetGuid());
    }

    [Fact]
    public async Task Default_categories_exist()
    {
        var list = await (await f.As(f.ExecA).GetAsync($"/api/projects/{f.Project}/document-categories", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var names = list.EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(8, names.Count);
        Assert.Contains("قراردادها", names);
        Assert.Contains("ازبیلت", names);
    }

    [Fact]
    public async Task Upload_and_download_are_byte_exact_and_audited()
    {
        var (id, version) = await Upload(f.ExecA);
        var r = await f.As(f.ExecA).GetAsync($"/api/document-versions/{version}/download", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(Pdf, await r.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("attachment", r.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());

        var versions = await (await f.As(f.ExecA).GetAsync($"/api/documents/{id}/versions", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Pdf)), versions[0].GetProperty("sha256").GetString());

        await using var c = new NpgsqlConnection(ApiFixture.Cs);
        await c.OpenAsync(Ct);
        await using var q = new NpgsqlCommand($"SELECT count(*) FROM audit_log WHERE entity_id='{id}' AND action IN ('upload','download')", c);
        Assert.Equal(2L, await q.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Other_company_cannot_list_or_download()
    {
        var (id, version) = await Upload(f.ExecA, title: "company a only");
        var text = await (await f.As(f.ExecB).GetAsync($"/api/projects/{f.Project}/documents", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(id.ToString(), text);
        Assert.Equal(HttpStatusCode.NotFound, (await f.As(f.ExecB).GetAsync($"/api/document-versions/{version}/download", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.As(f.ExecB).PostAsync($"/api/documents/{id}/versions", Form(Pdf, "x.pdf"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Executor_cannot_place_document_in_other_company()
    {
        var (id, _) = await Upload(f.ExecA, f.OrgB);
        var mine = await (await f.As(f.ExecA).GetAsync($"/api/projects/{f.Project}/documents", Ct)).Content.ReadAsStringAsync(Ct);
        var theirs = await (await f.As(f.ExecB).GetAsync($"/api/projects/{f.Project}/documents", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains(id.ToString(), mine);
        Assert.DoesNotContain(id.ToString(), theirs);
    }

    [Fact]
    public async Task Viewer_cannot_upload() => Assert.Equal(HttpStatusCode.Forbidden,
        (await f.As(f.Viewer).PostAsync($"/api/projects/{f.Project}/documents", Form(Pdf, "a.pdf"), Ct)).StatusCode);

    [Theory]
    [InlineData("virus.exe")]
    [InlineData("report.pdf.exe")]
    [InlineData("page.html")]
    [InlineData("script.svg")]
    public async Task Dangerous_types_are_rejected(string name) => Assert.Equal(HttpStatusCode.UnsupportedMediaType,
        (await f.As(f.Pm).PostAsync($"/api/projects/{f.Project}/documents", Form(Pdf, name), Ct)).StatusCode);

    [Fact]
    public async Task Fake_pdf_is_rejected() => Assert.Equal(HttpStatusCode.UnsupportedMediaType,
        (await f.As(f.Pm).PostAsync($"/api/projects/{f.Project}/documents", Form("MZ not a pdf"u8.ToArray(), "fake.pdf"), Ct)).StatusCode);

    [Fact]
    public async Task Path_in_file_name_is_removed()
    {
        var r = await f.As(f.Pm).PostAsync($"/api/projects/{f.Project}/documents", Form(Pdf, "../../evil.pdf"), Ct);
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var versions = await (await f.As(f.Pm).GetAsync($"/api/documents/{id}/versions", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("evil.pdf", versions[0].GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task New_version_keeps_old_version()
    {
        var (id, first) = await Upload(f.Pm, f.OrgA);
        var second = Encoding.ASCII.GetBytes("%PDF-1.7 second");
        var r = await f.As(f.ExecA).PostAsync($"/api/documents/{id}/versions", Form(second, "plan-v2.pdf"), Ct);
        Assert.Equal(2, (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("versionNo").GetInt32());
        Assert.Equal(Pdf, await (await f.As(f.ExecA).GetAsync($"/api/document-versions/{first}/download", Ct)).Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Delete_is_permissioned_and_soft()
    {
        var (id, version) = await Upload(f.ExecA);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As(f.ExecA).DeleteAsync($"/api/documents/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await f.As(f.Pm).DeleteAsync($"/api/documents/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.As(f.Pm).GetAsync($"/api/document-versions/{version}/download", Ct)).StatusCode);
        await using var c = new NpgsqlConnection(ApiFixture.Cs);
        await c.OpenAsync(Ct);
        await using var q = new NpgsqlCommand($"SELECT count(*) FROM document_versions WHERE document_id='{id}'", c);
        Assert.Equal(1L, await q.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Category_with_document_cannot_be_deleted()
    {
        var created = await f.As(f.Pm).PostAsJsonAsync($"/api/projects/{f.Project}/document-categories", new { name = "دسته " + Guid.NewGuid() }, Ct);
        var category = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await f.As(f.Pm).PostAsync($"/api/projects/{f.Project}/documents", Form(Pdf, "x.pdf", ("categoryId", category.ToString()), ("organizationId", f.OrgB.ToString())), Ct);
        Assert.Equal(HttpStatusCode.Conflict, (await f.As(f.Pm).DeleteAsync($"/api/projects/{f.Project}/document-categories/{category}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Executor_cannot_change_categories() => Assert.Equal(HttpStatusCode.Forbidden,
        (await f.As(f.ExecA).PostAsJsonAsync($"/api/projects/{f.Project}/document-categories", new { name = "x" }, Ct)).StatusCode);
}
