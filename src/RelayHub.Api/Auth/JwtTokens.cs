using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RelayHub.Api.Startup;
using RelayHub.Core.Domain;

namespace RelayHub.Api.Auth;

/// <summary>Claim names in RelayHub tokens (short JWT names; inbound mapping to the long Microsoft URIs is turned off).</summary>
public static class RelayHubClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Role = "role";
    public const string Workspace = "ws";
    public const string WorkspaceName = "wsn";
    public const string Demo = "demo";
}

public static class AuthPolicies
{
    /// <summary>Role Owner: configuration writes (sources, destinations, routes, rotate-key).</summary>
    public const string Owner = "Owner";
    /// <summary>Role Owner or Demo: everything that reads, plus the actions that are mocked in the Demo workspace.</summary>
    public const string Member = "Member";
}

/// <summary>What the dashboard knows about the signed-in user; the same shape in login, demo and /auth/me.</summary>
public sealed record UserDto(int Id, string Email, string Role, int WorkspaceId, string WorkspaceName, bool IsDemo);

/// <summary>
/// Issues and validates the dashboard's HS256 bearer tokens. Owner tokens live 7 days, Demo tokens 2 hours. The workspace travels in
/// the token (claim <c>ws</c>) and is the only source of the ambient workspace, so a user can never address another tenant.
/// </summary>
public sealed class JwtTokens
{
    public const string Issuer = "relayhub";
    public const string Audience = "relayhub";
    public const int MinKeyLength = 32;
    public static readonly TimeSpan OwnerLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan DemoLifetime = TimeSpan.FromHours(2);

    private const string DevSigningKey = "dev-only-jwt-signing-key-change-me-before-anyone-can-see-it";

    private readonly SymmetricSecurityKey _key;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = true };

    public JwtTokens(IOptions<RelayHubOptions> options, IHostEnvironment environment, ILogger<JwtTokens> log)
        : this(ResolveKey(options.Value.JwtSigningKey, environment.IsDevelopment(), log)) { }

    /// <summary>For tests and tools: a token service over an explicit key.</summary>
    public JwtTokens(string signingKey)
    {
        if (signingKey.Length < MinKeyLength) throw new ArgumentException($"The JWT signing key must be at least {MinKeyLength} characters.", nameof(signingKey));
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        ValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = _key,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = RelayHubClaims.Subject,
            RoleClaimType = RelayHubClaims.Role,
        };
    }

    public TokenValidationParameters ValidationParameters { get; }

    public (string Token, DateTimeOffset ExpiresAt) Issue(User user, Workspace workspace, DateTimeOffset? now = null)
    {
        var issuedAt = now ?? DateTimeOffset.UtcNow;
        var expiresAt = issuedAt + (user.Role == UserRole.Demo ? DemoLifetime : OwnerLifetime);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [RelayHubClaims.Subject] = user.Id.ToString(),
                [RelayHubClaims.Email] = user.Email,
                [RelayHubClaims.Role] = user.Role.ToString(),
                [RelayHubClaims.Workspace] = workspace.Id.ToString(),
                [RelayHubClaims.WorkspaceName] = workspace.Name,
                [RelayHubClaims.Demo] = workspace.IsDemo ? "true" : "false",
            },
        };
        return (_handler.CreateToken(descriptor), expiresAt);
    }

    /// <summary>Validates a token the way the bearer middleware does; null when it is invalid or expired.</summary>
    public async Task<ClaimsPrincipal?> ValidateAsync(string token)
    {
        var result = await _handler.ValidateTokenAsync(token, ValidationParameters);
        return result.IsValid ? new ClaimsPrincipal(result.ClaimsIdentity) : null;
    }

    public static UserDto ToDto(User user, Workspace workspace)
        => new(user.Id, user.Email, user.Role.ToString(), workspace.Id, workspace.Name, workspace.IsDemo);

    private static string ResolveKey(string configured, bool isDevelopment, ILogger log)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.Length < MinKeyLength)
                throw new InvalidOperationException($"RelayHub:JwtSigningKey must be at least {MinKeyLength} characters.");
            return configured;
        }
        if (!isDevelopment)
            throw new InvalidOperationException("RelayHub:JwtSigningKey must be configured outside Development.");
        log.LogWarning("RelayHub:JwtSigningKey is not set; using the built-in development key.");
        return DevSigningKey;
    }
}

public static class ClaimsPrincipalExtensions
{
    public static int? UserId(this ClaimsPrincipal user) => int.TryParse(user.FindFirstValue(RelayHubClaims.Subject), out var id) ? id : null;

    public static int? WorkspaceId(this ClaimsPrincipal user) => int.TryParse(user.FindFirstValue(RelayHubClaims.Workspace), out var id) ? id : null;

    public static bool IsDemo(this ClaimsPrincipal user) => string.Equals(user.FindFirstValue(RelayHubClaims.Demo), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>user</c> object of the contract, read from the token (no database round trip).</summary>
    public static UserDto? ToUserDto(this ClaimsPrincipal user)
    {
        if (user.UserId() is not { } id || user.WorkspaceId() is not { } ws) return null;
        return new UserDto(id, user.FindFirstValue(RelayHubClaims.Email) ?? "", user.FindFirstValue(RelayHubClaims.Role) ?? "",
            ws, user.FindFirstValue(RelayHubClaims.WorkspaceName) ?? "", user.IsDemo());
    }
}
