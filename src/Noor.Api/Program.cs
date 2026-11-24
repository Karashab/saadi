using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Noor.Api;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var connectionString = config.GetConnectionString("Noor")
    ?? throw new InvalidOperationException("ConnectionStrings:Noor is required");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NoorDb>();
builder.Services.AddSingleton<DeadlineJob>();
builder.Services.AddSingleton<IFileStore, LocalFileStore>();
builder.Services.AddSingleton<IVirusScanner, ClamAvScanner>();
if (config.GetValue("Worker:Enabled", true)) builder.Services.AddHostedService<DeadlineWorker>();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    var authority = config["Auth:Authority"];
    var testKey = config["Auth:TestSigningKey"];
    if (!string.IsNullOrEmpty(authority))
    {
        // Authority is the public issuer; metadata may be fetched over the internal network.
        options.Authority = authority;
        options.MetadataAddress = config["Auth:MetadataAddress"] ?? $"{authority.TrimEnd('/')}/.well-known/openid-configuration";
        options.RequireHttpsMetadata = options.MetadataAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = authority.TrimEnd('/'),
            ValidAudience = config["Auth:Audience"] ?? throw new InvalidOperationException("Auth:Audience is required"),
            NameClaimType = "preferred_username",
        };
    }
    else if (!string.IsNullOrEmpty(testKey) && builder.Environment.IsEnvironment("Testing"))
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = "noor-test", ValidAudience = "noor-api",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(testKey)),
        };
    }
    else
    {
        throw new InvalidOperationException("Auth:Authority is required");
    }
});
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "same-origin";
    headers.XFrameOptions = "DENY";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/config.json", () => Results.Json(new
{
    authority = config["Auth:PublicAuthority"] ?? config["Auth:Authority"],
    clientId = config["Auth:ClientId"] ?? "noor-web",
})).AllowAnonymous();
app.MapNoor();
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
