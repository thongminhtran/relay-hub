using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RelayHub.Core.Channels;
using RelayHub.Core.Domain;
using RelayHub.Infrastructure;
using RelayHub.Infrastructure.Connectors;
using RelayHub.Infrastructure.Data;
using RelayHub.Infrastructure.Dispatch;
using static RelayHub.Api.Endpoints.EndpointHelpers;

namespace RelayHub.Api.Endpoints;

public static partial class DestinationEndpoints
{
    public sealed record CreateDestinationRequest(string Name, string Type, JsonElement? Config);
    public sealed record UpdateDestinationRequest(string? Name, JsonElement? Config, bool? IsEnabled);
    public sealed record DestinationDto(int Id, string Type, string Name, JsonElement Config, bool IsEnabled);

    public static IEndpointRouteBuilder MapDestinationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/destinations").WithTags("Configuration");

        group.MapGet("/", ListAsync).WithName("ListDestinations");
        group.MapPost("/", CreateAsync).WithName("CreateDestination")
             .WithSummary("Create a destination. Configs: discord { webhookUrl, username? } · teams { webhookUrl } (Workflows webhook) · email { to, from?, subject? } (Resend) · telegram { botToken, chatId } · http { url, method, headers?, bodyTemplate? } (https only, SSRF-guarded, owner workspaces only).");
        group.MapGet("/{id:int}", GetAsync).WithName("GetDestination");
        group.MapPut("/{id:int}", UpdateAsync).WithName("UpdateDestination");
        group.MapDelete("/{id:int}", DeleteAsync).WithName("DeleteDestination");
        group.MapPost("/{id:int}/test", TestAsync).WithName("TestDestination").WithSummary("Send a test message to this destination.");

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

        // Arbitrary outbound URLs are an owner-only feature: the public demo must not become a free HTTP relay.
        if (type == DestinationType.Http && await db.Workspaces.AnyAsync(w => w.Id == ambient.WorkspaceIdOrDefault() && w.IsDemo, ct))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "HTTP destinations are not available in the demo workspace");

        var (configJson, error) = await ValidateConfigAsync(type, request.Config, ct);
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
            var (configJson, error) = await ValidateConfigAsync(destination.Type, config, ct);
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

    private static async Task<IResult> TestAsync(int id, RelayHubDbContext db, DeliveryDispatcher dispatcher, CancellationToken ct)
    {
        var destination = await db.Destinations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (destination is null) return NotFound("Destination");

        var result = await dispatcher.SendTestAsync(destination, ct);
        return Results.Ok(new { result.Succeeded, result.HttpStatus, result.Error });
    }

    /// <summary>Shape rules, then (HTTP only) the DNS half of the SSRF guard — refusing a URL that resolves to a private address before it is stored.</summary>
    private static async Task<(string? ConfigJson, string? Error)> ValidateConfigAsync(DestinationType type, JsonElement? config, CancellationToken ct)
    {
        var (configJson, error) = ValidateConfig(type, config);
        if (error is not null || type != DestinationType.Http) return (configJson, error);

        using var doc = JsonDocument.Parse(configJson!);
        var verdict = await SsrfGuard.ResolveAsync(doc.RootElement.GetProperty("url").GetString(), ct);
        return verdict.Allowed ? (configJson, null) : (null, $"url refused: {verdict.Error}");
    }

    /// <summary>Per-connector config rules. Returns normalized JSON to store, or an error message.</summary>
    private static (string? ConfigJson, string? Error) ValidateConfig(DestinationType type, JsonElement? config)
    {
        if (config is not { ValueKind: JsonValueKind.Object } cfg) return (null, "Config must be a JSON object");

        switch (type)
        {
            case DestinationType.Discord:
            {
                var url = GetString(cfg, "webhookUrl");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                    || uri.Host is not ("discord.com" or "discordapp.com") || !uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal))
                    return (null, "webhookUrl must be a Discord incoming-webhook URL (https://discord.com/api/webhooks/...)");
                var username = GetString(cfg, "username");
                return (JsonSerializer.Serialize(new { webhookUrl = url, username }), null);
            }
            case DestinationType.Teams:
            {
                var url = GetString(cfg, "webhookUrl");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                    || !(uri.Host.EndsWith(".logic.azure.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".webhook.office.com", StringComparison.OrdinalIgnoreCase)))
                    return (null, "webhookUrl must be a Teams Workflows webhook URL (host *.logic.azure.com or *.webhook.office.com)");
                return (JsonSerializer.Serialize(new { webhookUrl = url }), null);
            }
            case DestinationType.Email:
            {
                var to = GetString(cfg, "to")?.Trim();
                if (!SourceConfigs.IsEmail(to)) return (null, "to must be a valid email address");
                var from = GetString(cfg, "from")?.Trim();
                if (!string.IsNullOrEmpty(from) && !MailAddress.TryCreate(from, out _)) return (null, "from must be an email address, optionally as \"Name <address>\"");
                var subject = GetString(cfg, "subject")?.Trim();
                if (subject is { Length: > 200 }) return (null, "subject must be at most 200 characters");
                return (JsonSerializer.Serialize(new { to, from = NullIfEmpty(from), subject = NullIfEmpty(subject) }), null);
            }
            case DestinationType.Telegram:
            {
                var botToken = GetString(cfg, "botToken")?.Trim();
                if (!SourceConfigs.IsTelegramBotToken(botToken)) return (null, "botToken must be a Telegram bot token from @BotFather, e.g. 123456789:AAH...");
                var chatId = cfg.TryGetProperty("chatId", out var c) ? c.ValueKind switch { JsonValueKind.Number => c.GetRawText(), JsonValueKind.String => c.GetString()?.Trim(), _ => null } : null;
                if (string.IsNullOrEmpty(chatId) || !TelegramChatIdPattern().IsMatch(chatId)) return (null, "chatId must be a numeric chat id (negative for groups) or a public @channelname");
                return (JsonSerializer.Serialize(new { botToken, chatId }), null);
            }
            case DestinationType.Http:
            {
                var url = GetString(cfg, "url")?.Trim();
                if (SsrfGuard.CheckUrl(url, out _) is { } urlError) return (null, urlError);
                var method = (GetString(cfg, "method")?.Trim().ToUpperInvariant()) ?? "POST";
                if (!HttpConnector.AllowedMethods.Contains(method)) return (null, $"method must be one of {string.Join(", ", HttpConnector.AllowedMethods)}");

                Dictionary<string, string>? headers = null;
                if (cfg.TryGetProperty("headers", out var h) && h.ValueKind != JsonValueKind.Null)
                {
                    if (h.ValueKind != JsonValueKind.Object) return (null, "headers must be an object of string values");
                    headers = [];
                    foreach (var p in h.EnumerateObject())
                    {
                        if (p.Value.ValueKind != JsonValueKind.String || !HeaderNamePattern().IsMatch(p.Name) || p.Value.GetString()!.Length > 1000)
                            return (null, $"header {p.Name} must have a token name and a string value of at most 1000 characters");
                        if (HttpConnector.ReservedHeaders.Contains(p.Name.ToLowerInvariant())) return (null, $"header {p.Name} cannot be set");
                        headers[p.Name] = p.Value.GetString()!;
                    }
                    if (headers.Count > 20) return (null, "at most 20 headers");
                }
                var bodyTemplate = GetString(cfg, "bodyTemplate");
                if (bodyTemplate is { Length: > 4000 }) return (null, "bodyTemplate must be at most 4000 characters");
                return (JsonSerializer.Serialize(new { url, method, headers, bodyTemplate = NullIfEmpty(bodyTemplate) }), null);
            }
            default:
                return (null, $"{type} destinations are not supported");
        }
    }

    [GeneratedRegex(@"^(-?\d{1,20}|@[A-Za-z0-9_]{5,32})$")]
    private static partial Regex TelegramChatIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,100}$")]
    private static partial Regex HeaderNamePattern();

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static DestinationDto ToDto(Destination d) => new(d.Id, d.Type.ToString().ToLowerInvariant(), d.Name, MaskConfig(d.ConfigJson), d.IsEnabled);
}
