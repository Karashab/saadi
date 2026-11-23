using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
if (config.GetValue("Worker:Enabled", true)) builder.Services.AddHostedService<DeadlineWorker>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    var authority = config["Auth:Authority"];
    var testKey = config["Auth:TestSigningKey"];
    if (!string.IsNullOrEmpty(authority))
    {
        options.Authority = authority;
        options.Audience = config["Auth:Audience"] ?? throw new InvalidOperationException("Auth:Audience is required");
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
app.UseAuthentication();
app.UseAuthorization();
app.MapNoor();
app.Run();

public partial class Program;
