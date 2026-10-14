using System.Security.Claims;
using RelayHub.Api.Auth;
using RelayHub.Infrastructure;
using RelayHub.Infrastructure.Realtime;
using static RelayHub.Api.Endpoints.EndpointHelpers;

namespace RelayHub.Api.Endpoints;

public static class RealtimeEndpoints
{
    public sealed record NegotiateResponse(string Url, string AccessToken);

    public static IEndpointRouteBuilder MapRealtimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/realtime").WithTags("Realtime").RequireAuthorization(AuthPolicies.Member)
           .MapPost("/negotiate", NegotiateAsync).WithName("RealtimeNegotiate")
           .WithSummary("Azure SignalR (serverless) negotiation: the dashboard connects to the returned url with the access token and receives eventCreated / deliveryUpdated / replySent for its workspace.")
           .Produces<NegotiateResponse>()
           .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }

    private static async Task<IResult> NegotiateAsync(ClaimsPrincipal user, HttpContext http, AmbientWorkspace ambient, ILogger<SignalRRealtimeNotifier> log, CancellationToken ct)
    {
        var negotiator = http.RequestServices.GetService<IRealtimeNegotiator>();
        if (negotiator is null) return Unavailable("SignalR is not configured (SignalR:Endpoint or SignalR:ConnectionString).");
        if (user.UserId() is not { } userId) return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid token");

        try
        {
            var negotiation = await negotiator.NegotiateAsync(userId.ToString(), ambient.WorkspaceIdOrDefault(), ct);
            return Results.Ok(new NegotiateResponse(negotiation.Url, negotiation.AccessToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "SignalR negotiation failed");
            return Unavailable("The realtime service did not answer; the dashboard falls back to manual refresh.");
        }
    }

    private static IResult Unavailable(string detail)
        => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, type: ProblemTypes.RealtimeUnavailable, title: "Realtime updates are unavailable", detail: detail);
}
