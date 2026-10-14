using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RelayHub.Api.Auth;
using RelayHub.Core.Domain;
using RelayHub.Infrastructure.Data;
using static RelayHub.Api.Endpoints.EndpointHelpers;

namespace RelayHub.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string? Email, string? Password);
    public sealed record DemoRequest(string? TurnstileToken);
    public sealed record TokenResponse(string Token, DateTimeOffset ExpiresAt, UserDto User);

    /// <summary>Email of the row that backs anonymous demo tokens. It has an unguessable password: nobody logs in as it directly.</summary>
    public const string DemoUserEmail = "demo@relayhub.local";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync).WithName("Login").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Login)
             .WithSummary("Owner login. Returns a 7-day bearer token; send it as Authorization: Bearer <token>.")
             .Produces<TokenResponse>().ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status429TooManyRequests);
        group.MapPost("/demo", DemoAsync).WithName("DemoLogin").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Demo)
             .WithSummary("Anonymous 2-hour token for the public Demo workspace (Cloudflare Turnstile when configured).")
             .Produces<TokenResponse>().ProducesProblem(StatusCodes.Status503ServiceUnavailable).ProducesProblem(StatusCodes.Status429TooManyRequests);
        group.MapGet("/me", Me).WithName("Me").RequireAuthorization(AuthPolicies.Member).Produces<UserDto>();

        return app;
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, RelayHubDbContext db, IPasswordHasher<User> hasher, JwtTokens jwt, CancellationToken ct)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        var password = request.Password ?? "";
        if (email.Length == 0 || password.Length == 0) return ValidationProblem("email", "Email and password are required");

        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
        var verdict = user is null ? PasswordVerificationResult.Failed : hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (user is null || verdict == PasswordVerificationResult.Failed)
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid email or password");

        if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, password);
            await db.SaveChangesAsync(ct);
        }

        var workspace = await db.Workspaces.SingleAsync(w => w.Id == user.WorkspaceId, ct);
        return Results.Ok(Issue(jwt, user, workspace));
    }

    private static async Task<IResult> DemoAsync(DemoRequest? request, HttpContext http, RelayHubDbContext db, IPasswordHasher<User> hasher, TurnstileVerifier turnstile, JwtTokens jwt, CancellationToken ct)
    {
        if (!await turnstile.VerifyAsync(request?.TurnstileToken, http.Connection.RemoteIpAddress?.ToString(), ct))
            return ValidationProblem("turnstileToken", "The Turnstile challenge was missing or rejected; please try again");

        var workspace = await db.Workspaces.Where(w => w.IsDemo).OrderBy(w => w.Id).FirstOrDefaultAsync(ct);
        if (workspace is null)
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, type: ProblemTypes.DemoUnavailable,
                title: "The demo workspace is not available", detail: "No workspace with IsDemo = true exists.");

        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == DemoUserEmail, ct) ?? await CreateDemoUserAsync(db, hasher, workspace, ct);
        return Results.Ok(Issue(jwt, user, workspace));
    }

    private static IResult Me(ClaimsPrincipal principal)
        => principal.ToUserDto() is { } user ? Results.Ok(user) : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid token");

    private static TokenResponse Issue(JwtTokens jwt, User user, Workspace workspace)
    {
        var (token, expiresAt) = jwt.Issue(user, workspace);
        return new TokenResponse(token, expiresAt, JwtTokens.ToDto(user, workspace));
    }

    /// <summary>The seeder normally creates this row; this is the fallback for a database that was migrated without seeding.</summary>
    public static async Task<User> CreateDemoUserAsync(RelayHubDbContext db, IPasswordHasher<User> hasher, Workspace demo, CancellationToken ct)
    {
        var user = new User { Email = DemoUserEmail, PasswordHash = "", Role = UserRole.Demo, WorkspaceId = demo.Id };
        user.PasswordHash = hasher.HashPassword(user, Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
            return user;
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.Entry(user).State = EntityState.Detached;
            return await db.Users.SingleAsync(u => u.Email == DemoUserEmail, ct); // another request won the race
        }
    }
}
