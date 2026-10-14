using RelayHub.Core.Domain;
using RelayHub.Infrastructure.Connectors;

namespace RelayHub.UnitTests;

public class DemoSinkConnectorTests
{
    private static readonly Event Evt = new() { WorkspaceId = 2, SourceId = 1, ExternalId = "x", Channel = "web", CorrelationId = "corr" };

    private static Destination Destination(string configJson) => new() { WorkspaceId = 2, Type = DestinationType.Discord, Name = "Flaky endpoint (mock)", ConfigJson = configJson };

    [Theory]
    [InlineData("""{"webhookUrl":"https://discord.com/api/webhooks/0/demo","username":"RelayHub demo"}""")]
    [InlineData("""{"webhookUrl":"https://discord.com/api/webhooks/0/flaky","failing":false}""")]
    [InlineData("""{"failing":"true"}""")] // only a JSON boolean counts
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("not json")]
    public async Task Healthy_destination_is_delivered_with_200(string configJson)
    {
        var result = await new DemoSinkConnector().SendAsync(Destination(configJson), "hello", Evt, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(200, result.HttpStatus);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Failing_destination_answers_a_retryable_503()
    {
        var result = await new DemoSinkConnector().SendAsync(
            Destination("""{"webhookUrl":"https://discord.com/api/webhooks/0/flaky","username":"flaky","failing":true}"""), "hello", Evt, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(503, result.HttpStatus);
        Assert.Equal(DemoSinkConnector.OutageError, result.Error);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task Sink_takes_a_realistic_moment()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await new DemoSinkConnector().SendAsync(Destination("{}"), "hello", Evt, CancellationToken.None);
        Assert.InRange(sw.ElapsedMilliseconds, 30, 2000);
    }
}
