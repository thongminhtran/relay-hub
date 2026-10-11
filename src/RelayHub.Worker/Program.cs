using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

// Week 2: builder.Services.AddRelayHubInfrastructure(...), connectors, Application Insights.

builder.Build().Run();
