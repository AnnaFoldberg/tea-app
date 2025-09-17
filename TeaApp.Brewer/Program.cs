using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using TeaApp.Brewer.Worker;

var builder = WebApplication.CreateBuilder(args);

// Read required environment variables (fail fast if missing)
static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing environment variable: {name}");

var rabbitHost = RequireEnv("RABBIT_HOST");
var rabbitUser = RequireEnv("RABBIT_USER");
var rabbitPass = RequireEnv("RABBIT_PASS");

// Run background worker
builder.Services.AddHostedService(serviceProvider =>
    new Worker(rabbitHost, rabbitUser, rabbitPass));

// Health checks (live/ready)
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddRabbitMQ(
        serviceProvider => new ConnectionFactory { HostName = rabbitHost, UserName = rabbitUser, Password = rabbitPass }
                    .CreateConnectionAsync(),
        name: "rabbitmq",
        tags: new[] { "ready" }
    );

// Graceful shutdown: Give background services time to stop cleanly
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));

var app = builder.Build();

// Liveness: process is up
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
// Readiness: RabbitMQ reachable
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });

app.Run();