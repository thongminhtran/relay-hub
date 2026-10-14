using System.Text.Json;
using RelayHub.Api.Auth;
using RelayHub.Core.Channels;
using RelayHub.Core.Domain;

namespace RelayHub.UnitTests;

public class JwtTokenTests
{
    private const string Key = "unit-test-jwt-signing-key-that-is-long-enough-for-hs256";
    private static readonly Workspace Studio = new() { Id = 1, Name = "Studio" };
    private static readonly Workspace Demo = new() { Id = 2, Name = "Demo", IsDemo = true };
    private static readonly User Owner = new() { Id = 7, Email = "owner@relayhub.local", PasswordHash = "x", Role = UserRole.Owner, WorkspaceId = 1 };
    private static readonly User DemoUser = new() { Id = 8, Email = "demo@relayhub.local", PasswordHash = "x", Role = UserRole.Demo, WorkspaceId = 2 };

    [Fact]
    public async Task Owner_token_round_trips_every_claim_and_lives_seven_days()
    {
        var jwt = new JwtTokens(Key);
        var issuedAt = DateTimeOffset.Parse("2026-10-14T10:00:00Z");

        var (token, expiresAt) = jwt.Issue(Owner, Studio, issuedAt);
        var principal = await jwt.ValidateAsync(token);

        Assert.Equal(issuedAt.AddDays(7), expiresAt);
        Assert.NotNull(principal);
        Assert.Equal(7, principal.UserId());
        Assert.Equal(1, principal.WorkspaceId());
        Assert.False(principal.IsDemo());
        Assert.True(principal.IsInRole("Owner"));
        Assert.Equal("7", principal.Identity?.Name); // NameClaimType = sub
        var dto = principal.ToUserDto();
        Assert.Equal(new UserDto(7, "owner@relayhub.local", "Owner", 1, "Studio", false), dto);
        Assert.Equal(JwtTokens.ToDto(Owner, Studio), dto);
    }

    [Fact]
    public async Task Demo_token_lives_two_hours_and_carries_the_demo_flag()
    {
        var jwt = new JwtTokens(Key);
        var issuedAt = DateTimeOffset.UtcNow;

        var (token, expiresAt) = jwt.Issue(DemoUser, Demo, issuedAt);
        var principal = await jwt.ValidateAsync(token);

        Assert.Equal(issuedAt.AddHours(2), expiresAt);
        Assert.NotNull(principal);
        Assert.True(principal.IsDemo());
        Assert.True(principal.IsInRole("Demo"));
        Assert.False(principal.IsInRole("Owner"));
        Assert.Equal(2, principal.WorkspaceId());
    }

    [Fact]
    public async Task Tokens_signed_with_another_key_tampered_or_expired_are_rejected()
    {
        var jwt = new JwtTokens(Key);
        var other = new JwtTokens("another-unit-test-key-with-at-least-32-characters!!");

        var (foreign, _) = other.Issue(Owner, Studio);
        Assert.Null(await jwt.ValidateAsync(foreign));

        var (token, _) = jwt.Issue(Owner, Studio);
        var parts = token.Split('.');
        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(Convert.FromBase64String(Pad(parts[1])))!;
        payload["ws"] = "2"; // try to hop into the Demo workspace
        var tamperedPayload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Null(await jwt.ValidateAsync($"{parts[0]}.{tamperedPayload}.{parts[2]}"));

        var (expired, _) = jwt.Issue(Owner, Studio, DateTimeOffset.UtcNow.AddDays(-8));
        Assert.Null(await jwt.ValidateAsync(expired));

        Assert.Null(await jwt.ValidateAsync("not.a.jwt"));
    }

    [Fact]
    public void Short_keys_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new JwtTokens("too-short"));
    }

    [Fact]
    public void Source_config_placeholder_accepts_objects_and_nothing_else()
    {
        Assert.Equal(("{}", null), SourceConfigs.Validate(SourceType.Telegram, null));
        Assert.Equal(("{}", null), SourceConfigs.Validate(SourceType.Telegram, JsonSerializer.SerializeToElement((object?)null)));
        Assert.Equal(("""{"botToken":"abc"}""", null), SourceConfigs.Validate(SourceType.Telegram, JsonSerializer.SerializeToElement(new { botToken = "abc" })));
        var (json, error) = SourceConfigs.Validate(SourceType.Telegram, JsonSerializer.SerializeToElement("abc"));
        Assert.Null(json);
        Assert.NotNull(error);
    }

    private static string Pad(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }
}
