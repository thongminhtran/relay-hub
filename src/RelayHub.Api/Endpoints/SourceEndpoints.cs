using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelayHub.Api.Startup;
using RelayHub.Core.Domain;
using RelayHub.Core.Security;
using RelayHub.Infrastructure;
using RelayHub.Infrastructure.Data;
using static RelayHub.Api.Endpoints.EndpointHelpers;

namespace RelayHub.Api.Endpoints;

public static class SourceEndpoints
{
    public sealed record CreateSourceRequest(string Name, string? Type = "generic");
    public sealed record UpdateSourceRequest(string? Name, bool? IsEnabled);
    public sealed record SourceDto(int Id, string Type, string Name, string KeyId, bool IsEnabled, DateTimeOffset CreatedAt);

    public static IEndpointRouteBuilder MapSourceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sources").WithTags("Configuration");

        group.MapGet("/", ListAsync).WithName("ListSources");
        group.MapPost("/", CreateAsync).WithName("CreateSource")
             .WithSummary("Create a source. The response contains the API key exactly once; it is never stored.");
        group.MapGet("/{id:int}", GetAsync).WithName("GetSource");
        group.MapPut("/{id:int}", UpdateAsync).WithName("UpdateSource");
        group.MapPost("/{id:int}/rotate-key", RotateKeyAsync).WithName("RotateSourceKey")
             .WithSummary("Issue a new API key; the old one stops working immediately.");
        group.MapDelete("/{id:int}", DeleteAsync).WithName("DeleteSource");

        return app;
    }

    private static async Task<IResult> ListAsync(RelayHubDbContext db, CancellationToken ct)
        => Results.Ok(await db.Sources.AsNoTracking().OrderBy(s => s.Id).Select(s => ToDto(s)).ToListAsync(ct));

    private static async Task<IResult> GetAsync(int id, RelayHubDbContext db, CancellationToken ct)
        => await db.Sources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) is { } s ? Results.Ok(ToDto(s)) : NotFound("Source");

    private static async Task<IResult> CreateAsync(CreateSourceRequest request, RelayHubDbContext db, AmbientWorkspace ambient, IOptions<RelayHubOptions> options, CancellationToken ct)
    {
        var name = request.Name?.Trim().ToLowerInvariant() ?? "";
        if (!SlugPattern().IsMatch(name))
            return ValidationProblem("name", "Use 2-49 lowercase letters, digits or hyphens, e.g. website-contact");
        if (!TryParseEnum<SourceType>(request.Type ?? "generic", out var type))
            return ValidationProblem("type", $"Unknown source type. Allowed: {string.Join(", ", Enum.GetNames<SourceType>())}");
        if (type != SourceType.Generic)
            return ValidationProblem("type", "Only generic sources can be created through the API in this version; Meta, Telegram and email sources arrive in Week 4");
        if (await db.Sources.AnyAsync(s => s.Name == name, ct))
            return ValidationProblem("name", "A source with this name already exists in the workspace");

        var source = new Source { WorkspaceId = ambient.WorkspaceIdOrDefault(), Type = type, Name = name, KeyId = ApiKeys.NewKeyId() };
        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/sources/{source.Id}", new
        {
            source = ToDto(source),
            apiKey = ApiKeys.Create(source.KeyId, options.Value.ApiKeyMasterKey),
            ingestUrl = $"/api/v1/hooks/{source.Name}",
        });
    }

    private static async Task<IResult> UpdateAsync(int id, UpdateSourceRequest request, RelayHubDbContext db, CancellationToken ct)
    {
        var source = await db.Sources.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (source is null) return NotFound("Source");

        if (request.Name is not null)
        {
            var name = request.Name.Trim().ToLowerInvariant();
            if (!SlugPattern().IsMatch(name)) return ValidationProblem("name", "Use 2-49 lowercase letters, digits or hyphens");
            if (name != source.Name && await db.Sources.AnyAsync(s => s.Name == name, ct)) return ValidationProblem("name", "A source with this name already exists in the workspace");
            source.Name = name;
        }
        if (request.IsEnabled is { } enabled) source.IsEnabled = enabled;

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(source));
    }

    private static async Task<IResult> RotateKeyAsync(int id, RelayHubDbContext db, IOptions<RelayHubOptions> options, CancellationToken ct)
    {
        var source = await db.Sources.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (source is null) return NotFound("Source");

        source.KeyId = ApiKeys.NewKeyId();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { source = ToDto(source), apiKey = ApiKeys.Create(source.KeyId, options.Value.ApiKeyMasterKey) });
    }

    private static async Task<IResult> DeleteAsync(int id, RelayHubDbContext db, CancellationToken ct)
    {
        var source = await db.Sources.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (source is null) return NotFound("Source");

        await db.Routes.Where(r => r.SourceId == id).ExecuteDeleteAsync(ct);
        db.Sources.Remove(source);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static SourceDto ToDto(Source s) => new(s.Id, s.Type.ToString().ToLowerInvariant(), s.Name, s.KeyId, s.IsEnabled, s.CreatedAt);
}
