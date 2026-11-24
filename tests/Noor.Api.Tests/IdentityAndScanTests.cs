using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Noor.Api.Tests;

public class IdentityAndScanTests(ApiFixture f) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => CancellationToken.None;

    private static string Token(string subject, params (string Type, string Value)[] claims)
    {
        var all = new List<Claim> { new("sub", subject) };
        all.AddRange(claims.Select(c => new Claim(c.Type, c.Value)));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "noor-test", Audience = "noor-api", Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity(all),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFixture.Key)), SecurityAlgorithms.HmacSha256),
        });
    }

    private static HttpClient Client(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task First_sign_in_creates_user_without_any_access()
    {
        var subject = Guid.NewGuid().ToString();
        var name = "کاربر " + subject[..6];
        var client = Client(f.CreateClient(), Token(subject, ("name", name), ("preferred_username", "u" + subject[..8])));
        var projects = await (await client.GetAsync("/api/projects", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Empty(projects.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{f.Project}/tasks", Ct)).StatusCode);

        var found = await (await f.As(f.Pm).GetAsync($"/api/projects/{f.Project}/user-directory?q={Uri.EscapeDataString(name)}", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(name, Assert.Single(found.EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Disabled_user_is_not_reactivated_by_signing_in()
    {
        var subject = Guid.NewGuid().ToString();
        await using (var c = new NpgsqlConnection(ApiFixture.Cs))
        {
            await c.OpenAsync(Ct);
            await using var q = new NpgsqlCommand($"INSERT INTO users(identity_subject,display_name,is_active) VALUES ('{subject}','off',false)", c);
            await q.ExecuteNonQueryAsync(Ct);
        }
        var r = await Client(f.CreateClient(), Token(subject, ("name", "on"))).GetAsync("/api/projects", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Directory_is_for_managers_only() => Assert.Equal(HttpStatusCode.Forbidden,
        (await f.As(f.ExecA).GetAsync($"/api/projects/{f.Project}/user-directory?q=pm", Ct)).StatusCode);

    [Fact]
    public async Task Public_config_has_no_secrets()
    {
        var text = await (await f.CreateClient().GetAsync("/config.json", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(ApiFixture.Key, text);
        Assert.Contains("clientId", text);
    }

    private static (TcpListener Listener, Task Server) FakeClamd(Func<string, string> reply)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var received = new MemoryStream();
                var command = new byte[10];
                await stream.ReadExactlyAsync(command);
                var size = new byte[4];
                while (true)
                {
                    await stream.ReadExactlyAsync(size);
                    var length = (size[0] << 24) | (size[1] << 16) | (size[2] << 8) | size[3];
                    if (length == 0) break;
                    var chunk = new byte[length];
                    await stream.ReadExactlyAsync(chunk);
                    received.Write(chunk);
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply(Encoding.ASCII.GetString(received.ToArray())) + "\0"));
            }
        });
        return (listener, server);
    }

    private static MultipartFormDataContent Pdf(string body)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.7 " + body)), "file", "scan.pdf");
        return form;
    }

    [Fact]
    public async Task Infected_upload_is_rejected_and_clean_one_accepted()
    {
        var (listener, _) = FakeClamd(content => content.Contains("EICAR") ? "stream: Eicar-Test-Signature FOUND" : "stream: OK");
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var app = f.WithWebHostBuilder(b => { b.UseSetting("Scan:ClamAvHost", "127.0.0.1"); b.UseSetting("Scan:ClamAvPort", port.ToString()); b.UseSetting("Scan:Required", "true"); });
            var client = Client(app.CreateClient(), Token(f.Pm.ToString()));
            Assert.Equal((HttpStatusCode)422, (await client.PostAsync($"/api/projects/{f.Project}/documents", Pdf("EICAR"), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsync($"/api/projects/{f.Project}/documents", Pdf("clean"), Ct)).StatusCode);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Required_scan_fails_closed_when_antivirus_is_down()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var app = f.WithWebHostBuilder(b => { b.UseSetting("Scan:ClamAvHost", "127.0.0.1"); b.UseSetting("Scan:ClamAvPort", port.ToString()); b.UseSetting("Scan:Required", "true"); });
        var r = await Client(app.CreateClient(), Token(f.Pm.ToString())).PostAsync($"/api/projects/{f.Project}/documents", Pdf("x"), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }
}
