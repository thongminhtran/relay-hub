using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using RelayHub.Api.Endpoints;
using RelayHub.Api.Startup;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Trace;
using RelayHub.Core.Telemetry;
using RelayHub.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.Configure<RelayHubOptions>(builder.Configuration.GetSection(RelayHubOptions.SectionName));
builder.Services.AddRelayHubInfrastructure(builder.Configuration.GetConnectionString("RelayHub"));
builder.Services.AddRelayHubMessaging(builder.Configuration);
builder.Services.AddHealthChecks().AddCheck<SqlHealthCheck>("sql", tags: ["ready"]);
builder.Services.AddRateLimiter(DemoRateLimiting.Add);

// OpenTelemetry → Application Insights (Azure Monitor distro): requests, dependencies (SQL, HTTP, queue) and logs.
// The W3C trace id of each ingest request travels inside the queue message, so the worker's spans join the same trace.
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor().WithTracing(t => t.AddSource(RelayHubTelemetry.SourceName));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

app.MapOpenApi();
app.MapScalarApiReference("/docs", options => options.WithTitle("RelayHub API"));

// Correlation id on every response + temporary workspace scoping until JWT auth lands (Week 3).
app.Use(async (context, next) =>
{
    var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
    context.Items["CorrelationId"] = correlationId;
    context.Response.Headers["X-Correlation-Id"] = correlationId;

    var ambient = context.RequestServices.GetRequiredService<AmbientWorkspace>();
    ambient.WorkspaceId = int.TryParse(context.Request.Headers["X-Workspace-Id"], out var id) ? id : 1;

    await next();
});

// Liveness never touches the database (serverless SQL must be allowed to sleep). Safe target for the keep-warm ping.
app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }))
   .WithTags("Operations").ExcludeFromDescription();

// Readiness includes the database. Only the dashboard wake-up screen calls this.
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });

app.MapIngestEndpoints();
app.MapEventEndpoints();
app.MapDeliveryEndpoints();
app.MapSourceEndpoints();
app.MapDestinationEndpoints();
app.MapRouteEndpoints();
app.MapDemoEndpoints();

await DevSeeder.RunAsync(app);

app.Run();

/// <summary>Exposed so integration tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
