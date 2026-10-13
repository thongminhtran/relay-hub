using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;
using RelayHub.Core.Telemetry;
using RelayHub.Infrastructure;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddRelayHubInfrastructure(builder.Configuration.GetConnectionString("RelayHub"));
builder.Services.AddRelayHubMessaging(builder.Configuration);

// OpenTelemetry → Application Insights. Each function starts a span whose parent is the traceparent carried in the queue
// message, so "API → queue → worker → Discord" is one end-to-end trace in the portal.
var telemetry = builder.Services.AddOpenTelemetry().UseFunctionsWorkerDefaults();
telemetry.WithTracing(tracing =>
{
    tracing.AddSource(RelayHubTelemetry.SourceName).AddHttpClientInstrumentation();
    if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        tracing.AddAzureMonitorTraceExporter();
});

builder.Build().Run();
