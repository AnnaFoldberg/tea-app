using TeaApp.Api.GraphQL.Queries;
using TeaApp.Api.GraphQL.Mutations;
using TeaApp.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using HotChocolate.Subscriptions;

var builder = WebApplication.CreateBuilder(args);

// Read required environment variables (fail fast if missing)
static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing environment variable: {name}");

var rabbitHost = RequireEnv("RABBIT_HOST");
var rabbitUser = RequireEnv("RABBIT_USER");
var rabbitPass = RequireEnv("RABBIT_PASS");

// Health checks (live/ready)
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddRabbitMQ(
        sp => new ConnectionFactory { HostName = rabbitHost, UserName = rabbitUser, Password = rabbitPass }
                    .CreateConnectionAsync(),
        name: "rabbitmq",
        tags: new[] { "ready" }
    );

// Graceful shutdown: Give background services time to stop cleanly
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));

// Bind config
var azureAd   = builder.Configuration.GetSection("AzureAd");
var auth      = builder.Configuration.GetSection("Auth");

// Fail fast if anything is missing (clear message includes the expected env var name)
static string Require(IConfigurationSection s, string key)
    => s[key] ?? throw new InvalidOperationException(
        $"Missing configuration: {s.Path}:{key}. " +
        $"Set env var {s.Path.Replace(":", "__").ToUpperInvariant()}__{key.ToUpperInvariant()}.");

string tenantId = Require(azureAd, "TenantId");
string audience = Require(azureAd, "Audience");
string apiClientId = Require(azureAd, "ClientId");
string requiredScope = Require(auth, "RequiredScope");

// AuthN (authentication): Verifies the incoming JWT from Microsoft Entra
// Checks signature, issuer, audience, expiration, etc.
// If valid, the JWT Bearer authentication handler
// creates a ClaimsPrincipal (the user identity inside HttpContext.User)
builder.Services
  .AddAuthentication(JwtBearerDefaults.AuthenticationScheme) // enable JWT bearer as the default auth scheme
  .AddJwtBearer(options =>
{
    // Tell middleware where to fetch OpenID Connect metadata (issuer, keys, etc.)
    options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";

    // Keep JWT claims as-is (don’t map them to legacy Microsoft claim types)
    options.MapInboundClaims = false;

    // Build list of valid audiences that this API should accept
    var validAudiences = new List<string>{audience, $"api://{apiClientId}", apiClientId!};

    var v2Issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0";

    // Token validation rules
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidAudiences = validAudiences,
        ValidIssuers = new[] { v2Issuer }
    };
});

// AuthZ (authorization): Decides whether that authenticated user
// has the right claims to access a given resource.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireApiScope", policy =>
        policy.RequireAssertion(ctx =>
        {
            var scp = ctx.User.FindFirst("scp")?.Value;
            if (string.IsNullOrEmpty(scp))
                return false;

            return scp.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                      .Contains(requiredScope, StringComparer.OrdinalIgnoreCase);
        }));
});
// Set up GraphQL Server + HotChocolate GraphQL with auth
builder.Services
    .AddGraphQLServer()
    .AddAuthorization()
    .AddQueryType(d => d.Name("Query"))
        .AddTypeExtension<TeaQueries>()
        .AddTypeExtension<OrderQueries>()
    .AddMutationType(d => d.Name("Mutation"))
        .AddTypeExtension<OrderMutations>()
    .AddSubscriptionType(d => d.Name("Subscription"))
        .AddTypeExtension<TeaApp.Api.GraphQL.Subscriptions.OrderSubscriptions>()
    .AddInMemorySubscriptions();

// Register services
// - IEventPublisher → RabbitMqPublisher: publishes domain events into RabbitMQ
// - IOrderStore → InMemoryOrderStore: simple in-memory state for queries/subscriptions
// - RabbitToSubscriptions: background worker bridging RabbitMQ messages into GraphQL subscriptions
builder.Services.AddSingleton<IEventPublisher>(_ =>
    new RabbitMqPublisher(rabbitHost, rabbitUser, rabbitPass));
builder.Services.AddSingleton<IOrderStore, InMemoryOrderStore>();
builder.Services.AddHostedService(serviceProvider =>
    new RabbitToSubscriptions(
        serviceProvider.GetRequiredService<ITopicEventSender>(),
        rabbitHost, rabbitUser, rabbitPass));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Needed for GraphQL subscriptions
app.UseWebSockets();

// Liveness: process is up
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
// Readiness: RabbitMQ reachable
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });

app.MapGet("/", () => "Tea API up");
app.MapGraphQL("/graphql");

app.Run();