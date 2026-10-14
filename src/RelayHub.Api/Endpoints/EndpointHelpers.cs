using System.Text.Json;
using System.Text.RegularExpressions;
using RelayHub.Infrastructure;

namespace RelayHub.Api.Endpoints;

/// <summary>Problem Details <c>type</c> URIs the dashboard switches on.</summary>
public static class ProblemTypes
{
    private const string Base = "https://relayhub.dev/problems/";
    public const string DemoUnavailable = Base + "demo-unavailable";
    public const string ReplyWindowClosed = Base + "reply-window-closed";
    public const string ReplyFailed = Base + "reply-failed";
    public const string ReplyUnsupported = Base + "reply-unsupported";
    public const string RealtimeUnavailable = Base + "realtime-unavailable";
}

internal static partial class EndpointHelpers
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,48}$")]
    public static partial Regex SlugPattern();

    /// <summary>
    /// Workspace for create operations. Comes from the token's <c>ws</c> claim; every endpoint that calls this sits behind
    /// RequireAuthorization, so a missing workspace is a wiring bug, never a reason to fall back to another tenant.
    /// </summary>
    public static int WorkspaceIdOrDefault(this AmbientWorkspace ambient)
        => ambient.WorkspaceId ?? throw new InvalidOperationException("No workspace on this request: the endpoint must require authorization.");

    public static IResult ValidationProblem(string field, string message)
        => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    public static IResult NotFound(string what) => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"{what} not found");

    public static bool TryParseEnum<T>(string? value, out T result) where T : struct, Enum
        => Enum.TryParse(value, ignoreCase: true, out result) && Enum.IsDefined(result);

    /// <summary>Returns the config with string values longer than 30 characters cut off, so webhook URLs and tokens are never echoed back.</summary>
    public static JsonElement MaskConfig(string configJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson);
        var masked = new Dictionary<string, object?>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            masked[p.Name] = p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 30 } s
                ? s[..30] + "…"
                : p.Value.Clone();
        }
        return JsonSerializer.SerializeToElement(masked);
    }
}
