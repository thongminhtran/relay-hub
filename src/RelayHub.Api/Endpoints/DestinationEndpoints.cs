using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RelayHub.Api.Auth;
using RelayHub.Core.Domain;
using RelayHub.Infrastructure;
using RelayHub.Infrastructure.Data;
using RelayHub.Infrastructure.Dispatch;
using static RelayHub.Api.Endpoints.EndpointHelpers;

namespace RelayHub.Api.Endpoints;

public static class DestinationEndpoints
{
    public sealed record CreateDestinationRequest(string Name, string Type, JsonElement? Config);
    public sealed record UpdateDestinationRequest(string? Name, JsonElement? Config, bool? IsEnabled);
    public sealed record DestinationDto(int Id, string Type, string Name, JsonElement Config, bool IsEnabled);

    public static IEndpointRouteBuilder MapDestinationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/destinations").WithTags("Configuration").RequireAuthorization(AuthPolicies.Member);

        group.MapGet("/", ListAsync).WithName("ListDestinations");
        group.MapPost("/", CreateAsync).WithName("CreateDestination").RequireAuthorization(AuthPolicies.Owner)
             .WithSummary("Create a destination. Discord config: { \"webhookUrl\": \"https://discord.com/api/webhooks/...\" }");
        group.MapGet("/{id:int}", GetAsync).WithName("GetDestination");
        group.MapPut("/{id:int}", UpdateAsync).WithName("UpdateDestination").RequireAuthorization(AuthPolicies.Owner);
        group.MapDelete("/{id:int}", DeleteAsync).WithName("DeleteDestination").RequireAuthorization(AuthPolicies.Owner);
        group.MapPost("/{id:int}/test", TestAsync).WithName("TestDestination").WithSummary("Send a test message to this destination (mocked in the Demo workspace: nothing leaves the system).");

        return app;
    }

    private static async Task<IResult> ListAsync(RelayHubDbContext db, CancellationToken ct)
        => Results.Ok((await db.Destinations.AsNoTracking().OrderBy(d => d.Id).ToListAsync(ct)).Select(ToDto));

    private static async Task<IResult> GetAsync(int id, RelayHubDbContext db, CancellationToken ct)
        => await db.Destinations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct) is { } d ? Results.Ok(ToDto(d)) : NotFound("Destination");

    private static async Task<IResult> CreateAsync(CreateDestinationRequest request, RelayHubDbContext db, AmbientWorkspace ambient, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 100) return ValidationProblem("name", "Name must be 1-100 characters");
        if (!TryParseEnum<DestinationType>(request.Type, out var type))
            return ValidationProblem("type", $"Unknown destination type. Allowed: {string.Join(", ", Enum.GetNames<DestinationType>())}");

        var (configJson, error) = ValidateConfig(type, request.Config);
        if (error is not null) return ValidationProblem("config", error);

        var destination = new Destination { WorkspaceId = ambient.WorkspaceIdOrDefault(), Type = type, Name = name, ConfigJson = configJson! };
        db.Destinations.Add(destination);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/destinations/{destination.Id}", ToDto(destination));
    }

    private static async Task<IResult> UpdateAsync(int id, UpdateDestinationRequest request, RelayHubDbContext db, CancellationToken ct)
    {
        var destination = await db.Destinations.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (destination is null) return NotFound("Destination");

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length is < 1 or > 100) return ValidationProblem("name", "Name must be 1-100 characters");
            destination.Name = name;
        }
        if (request.Config is { } config)
        {
            var (configJson, error) = ValidateConfig(destination.Type, config);
            if (error is not null) return ValidationProblem("config", error);
            destination.ConfigJson = configJson!;
        }
        if (request.IsEnabled is { } enabled) destination.IsEnabled = enabled;

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(destination));
    }

    private static async Task<IResult> DeleteAsync(int id, RelayHubDbContext db, CancellationToken ct)
    {
        var destination = await db.Destinations.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (destination is null) return NotFound("Destination");

        await db.Routes.Where(r => r.DestinationId == id).ExecuteDeleteAsync(ct);
        db.Destinations.Remove(destination);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TestAsync(int id, ClaimsPrincipal user, RelayHubDbContext db, DeliveryDispatcher dispatcher, CancellationToken ct)
    {
        var destination = await db.Destinations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (destination is null) return NotFound("Destination");

        // Demo workspace: the visitor sees a success, the webhook behind the destination is never called.
        if (user.IsDemo()) return Results.Ok(new { Succeeded = true, HttpStatus = (int?)200, Error = (string?)null, Mocked = true });

        var result = await dispatcher.SendTestAsync(destination, ct);
        return Results.Ok(new { result.Succeeded, result.HttpStatus, result.Error, Mocked = false });
    }

    /// <summary>Per-connector config rules. Returns normalized JSON to store, or an error message.</summary>
    private static (string? ConfigJson, string? Error) ValidateConfig(DestinationType type, JsonElement? config)
    {
        if (config is not { ValueKind: JsonValueKind.Object } cfg) return (null, "Config must be a JSON object");

        switch (type)
        {
            case DestinationType.Discord:
            {
                var url = cfg.TryGetProperty("webhookUrl", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                    || uri.Host is not ("discord.com" or "discordapp.com") || !uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal))
                    return (null, "webhookUrl must be a Discord incoming-webhook URL (https://discord.com/api/webhooks/...)");
                var username = cfg.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                return (JsonSerializer.Serialize(new { webhookUrl = url, username }), null);
            }
            default:
                return (null, $"{type} destinations are not available yet (Teams, Email and Telegram arrive in Week 4; HTTP needs the SSRF guard first)");
        }
    }

    private static DestinationDto ToDto(Destination d) => new(d.Id, d.Type.ToString().ToLowerInvariant(), d.Name, MaskConfig(d.ConfigJson), d.IsEnabled);
}
