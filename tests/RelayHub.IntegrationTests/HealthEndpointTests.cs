using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RelayHub.IntegrationTests;

/// <summary>Boots the real API in-process. /health must work with no database at all.</summary>
public class HealthEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_returns_ok_without_touching_the_database()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
        Assert.Contains("\"status\":\"ok\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ingest_without_api_key_is_unauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/hooks/website-contact", new { name = "Anna", message = "hi" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
