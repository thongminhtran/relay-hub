using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RelayHub.Api.Startup;

namespace RelayHub.IntegrationTests;

[Trait("Category", "Docker")]
[Collection(SqlServerCollection.Name)]
public class AuthTests(SqlServerApiFactory factory)
{
    [Fact]
    public async Task Owner_login_returns_a_token_and_the_user_shape()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = DevSeeder.DevOwnerEmail.ToUpperInvariant(), password = DevSeeder.DevOwnerPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("token").GetString()));
        var expiresAt = body.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange(expiresAt, DateTimeOffset.UtcNow.AddDays(6.9), DateTimeOffset.UtcNow.AddDays(7.1));
        var user = body.GetProperty("user");
        Assert.Equal(DevSeeder.DevOwnerEmail, user.GetProperty("email").GetString());
        Assert.Equal("Owner", user.GetProperty("role").GetString());
        Assert.Equal("Studio", user.GetProperty("workspaceName").GetString());
        Assert.False(user.GetProperty("isDemo").GetBoolean());
        Assert.True(user.GetProperty("id").GetInt32() > 0);
        Assert.True(user.GetProperty("workspaceId").GetInt32() > 0);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_are_401_problems()
    {
        using var client = factory.CreateClient();

        var wrong = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = DevSeeder.DevOwnerEmail, password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("application/problem+json", wrong.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Invalid email or password", (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());

        var unknown = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@relayhub.local", password = DevSeeder.DevOwnerPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        var missing = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "", password = "" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Missing_or_bad_bearer_is_401_and_health_docs_stay_public()
    {
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/api/v1/events", "/api/v1/conversations", "/api/v1/sources", "/api/v1/stats/summary", "/api/v1/auth/me" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.Contains("X-Correlation-Id"));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/realtime/negotiate", null)).StatusCode);

        using var forged = SqlServerApiFactory.WithBearer(factory.CreateClient(), "eyJhbGciOiJIUzI1NiJ9.e30.not-a-real-signature");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/v1/events")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Me_reflects_the_token()
    {
        using var owner = await factory.CreateOwnerClientAsync();
        var me = await owner.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal(DevSeeder.DevOwnerEmail, me.GetProperty("email").GetString());
        Assert.Equal("Owner", me.GetProperty("role").GetString());

        using var demo = await factory.CreateDemoClientAsync();
        var demoMe = await demo.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal("Demo", demoMe.GetProperty("role").GetString());
        Assert.True(demoMe.GetProperty("isDemo").GetBoolean());
        Assert.Equal("Demo", demoMe.GetProperty("workspaceName").GetString());
    }

    [Fact]
    public async Task Demo_token_lasts_two_hours_and_cannot_write_configuration()
    {
        using var anonymous = factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/demo", new { turnstileToken = "ignored-when-turnstile-is-not-configured" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.InRange(body.GetProperty("expiresAt").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddHours(1.9), DateTimeOffset.UtcNow.AddHours(2.1));
        Assert.Equal("Demo", body.GetProperty("user").GetProperty("role").GetString());

        using var demo = SqlServerApiFactory.WithBearer(factory.CreateClient(), body.GetProperty("token").GetString()!);
        var create = await demo.PostAsJsonAsync("/api/v1/sources", new { name = "demo-cannot-do-this" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("application/problem+json", create.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Forbidden, (await demo.PostAsJsonAsync("/api/v1/destinations", new { name = "x", type = "discord", config = new { } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await demo.PostAsJsonAsync("/api/v1/routes", new { sourceId = 1, destinationId = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await demo.PostAsync("/api/v1/sources/1/rotate-key", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await demo.DeleteAsync("/api/v1/sources/1")).StatusCode);

        // Reads work.
        Assert.Equal(HttpStatusCode.OK, (await demo.GetAsync("/api/v1/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await demo.GetAsync("/api/v1/events")).StatusCode);
    }

    [Fact]
    public async Task Demo_user_cannot_see_the_studio_workspace()
    {
        using var owner = await factory.CreateOwnerClientAsync();
        using var demo = await factory.CreateDemoClientAsync();

        // A source + event that exist only in Studio.
        var created = await owner.PostAsJsonAsync("/api/v1/sources", new { name = "isolation-form" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var source = await created.Content.ReadFromJsonAsync<JsonElement>();
        var sourceId = source.GetProperty("source").GetProperty("id").GetInt32();
        using var ingest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hooks/isolation-form") { Content = JsonContent.Create(new { name = "Dieu", email = "dieu@example.com", message = "studio-only secret" }) };
        ingest.Headers.Add("X-Api-Key", source.GetProperty("apiKey").GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await owner.SendAsync(ingest)).StatusCode);
        await factory.RunWorkerAsync();

        var ownerList = await owner.GetFromJsonAsync<JsonElement>("/api/v1/events?q=studio-only");
        Assert.Equal(1, ownerList.GetProperty("total").GetInt32());
        var eventId = ownerList.GetProperty("items")[0].GetProperty("id").GetInt64();
        var conversationId = ownerList.GetProperty("items")[0].GetProperty("conversationId").GetInt32();

        // The same ids answer 404 / empty for the demo token: the workspace comes from the token, nothing else.
        Assert.Equal(0, (await demo.GetFromJsonAsync<JsonElement>("/api/v1/events?q=studio-only")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await demo.GetAsync($"/api/v1/events/{eventId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await demo.GetAsync($"/api/v1/conversations/{conversationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await demo.GetAsync($"/api/v1/sources/{sourceId}")).StatusCode);
        Assert.DoesNotContain((await demo.GetFromJsonAsync<JsonElement>("/api/v1/sources")).EnumerateArray(), s => s.GetProperty("id").GetInt32() == sourceId);
        Assert.Equal(HttpStatusCode.NotFound, (await demo.PostAsJsonAsync($"/api/v1/conversations/{conversationId}/reply", new { text = "hi" })).StatusCode);
    }

    [Fact]
    public async Task Source_dto_carries_external_account_id_and_masked_config()
    {
        using var owner = await factory.CreateOwnerClientAsync();

        var created = await owner.PostAsJsonAsync("/api/v1/sources", new
        {
            name = "studio-instagram", type = "instagram", externalAccountId = "1784000000000001",
            config = new { pageAccessToken = "EAAB-very-long-secret-token-that-must-never-be-echoed-back-0123456789" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("source");
        Assert.Equal("instagram", dto.GetProperty("type").GetString());
        Assert.Equal("1784000000000001", dto.GetProperty("externalAccountId").GetString());
        var token = dto.GetProperty("config").GetProperty("pageAccessToken").GetString()!;
        Assert.EndsWith("…", token);
        Assert.DoesNotContain("0123456789", token);

        // Unique per type across workspaces; update accepts the same fields.
        var dup = await owner.PostAsJsonAsync("/api/v1/sources", new { name = "studio-instagram-2", type = "instagram", externalAccountId = "1784000000000001" });
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
        var invalidConfig = await owner.PostAsJsonAsync("/api/v1/sources", new { name = "bad-config", type = "telegram", config = "not-an-object" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidConfig.StatusCode);

        var id = dto.GetProperty("id").GetInt32();
        var updated = await owner.PutAsJsonAsync($"/api/v1/sources/{id}", new { externalAccountId = "1784000000000002", config = new { pageAccessToken = "short" }, isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1784000000000002", after.GetProperty("externalAccountId").GetString());
        Assert.Equal("short", after.GetProperty("config").GetProperty("pageAccessToken").GetString());
        Assert.False(after.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/sources/{id}")).StatusCode);
    }
}
