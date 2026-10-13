using Microsoft.EntityFrameworkCore;
using RelayHub.Infrastructure;
using RelayHub.Infrastructure.Data;
using static RelayHub.Api.Endpoints.EndpointHelpers;
using DomainRoute = RelayHub.Core.Domain.Route;

namespace RelayHub.Api.Endpoints;

public static class RouteEndpoints
{
    public sealed record CreateRouteRequest(int SourceId, int DestinationId, string? Template, string? Filter);
    public sealed record UpdateRouteRequest(string? Template, string? Filter, bool? IsEnabled);
    public sealed record RouteDto(int Id, int SourceId, int DestinationId, string Template, string? Filter, bool IsEnabled);

    public static IEndpointRouteBuilder MapRouteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/routes").WithTags("Configuration");

        group.MapGet("/", ListAsync).WithName("ListRoutes");
        group.MapPost("/", CreateAsync).WithName("CreateRoute")
             .WithSummary("Link a source to a destination. Template tokens: {{name}} {{email}} {{text}} {{channel}} {{source}} {{receivedAt}}");
        group.MapGet("/{id:int}", GetAsync).WithName("GetRoute");
        group.MapPut("/{id:int}", UpdateAsync).WithName("UpdateRoute");
        group.MapDelete("/{id:int}", DeleteAsync).WithName("DeleteRoute");

        return app;
    }

    private static async Task<IResult> ListAsync(RelayHubDbContext db, CancellationToken ct)
        => Results.Ok(await db.Routes.AsNoTracking().OrderBy(r => r.Id).Select(r => ToDto(r)).ToListAsync(ct));

    private static async Task<IResult> GetAsync(int id, RelayHubDbContext db, CancellationToken ct)
        => await db.Routes.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) is { } r ? Results.Ok(ToDto(r)) : NotFound("Route");

    private static async Task<IResult> CreateAsync(CreateRouteRequest request, RelayHubDbContext db, AmbientWorkspace ambient, CancellationToken ct)
    {
        if (!await db.Sources.AnyAsync(s => s.Id == request.SourceId, ct)) return ValidationProblem("sourceId", "Source not found in this workspace");
        if (!await db.Destinations.AnyAsync(d => d.Id == request.DestinationId, ct)) return ValidationProblem("destinationId", "Destination not found in this workspace");

        var template = string.IsNullOrWhiteSpace(request.Template) ? DomainRoute.DefaultTemplate : request.Template.Trim();
        if (template.Length > 2000) return ValidationProblem("template", "Template must be at most 2000 characters");

        var route = new DomainRoute
        {
            WorkspaceId = ambient.WorkspaceIdOrDefault(),
            SourceId = request.SourceId,
            DestinationId = request.DestinationId,
            Template = template,
            Filter = string.IsNullOrWhiteSpace(request.Filter) ? null : request.Filter.Trim(),
        };
        db.Routes.Add(route);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/routes/{route.Id}", ToDto(route));
    }

    private static async Task<IResult> UpdateAsync(int id, UpdateRouteRequest request, RelayHubDbContext db, CancellationToken ct)
    {
        var route = await db.Routes.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (route is null) return NotFound("Route");

        if (request.Template is not null)
        {
            var template = request.Template.Trim();
            if (template.Length is 0 or > 2000) return ValidationProblem("template", "Template must be 1-2000 characters");
            route.Template = template;
        }
        if (request.Filter is not null) route.Filter = string.IsNullOrWhiteSpace(request.Filter) ? null : request.Filter.Trim();
        if (request.IsEnabled is { } enabled) route.IsEnabled = enabled;

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(route));
    }

    private static async Task<IResult> DeleteAsync(int id, RelayHubDbContext db, CancellationToken ct)
    {
        var deleted = await db.Routes.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? NotFound("Route") : Results.NoContent();
    }

    private static RouteDto ToDto(DomainRoute r) => new(r.Id, r.SourceId, r.DestinationId, r.Template, r.Filter, r.IsEnabled);
}
