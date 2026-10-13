using RelayHub.Core.Messaging;
using RelayHub.Infrastructure.Dispatch;

namespace RelayHub.UnitTests;

public class BackoffTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 120)]
    [InlineData(3, 600)]
    [InlineData(4, 1800)]
    [InlineData(5, 3600)]
    [InlineData(9, 3600)] // replays beyond the table keep the last delay
    [InlineData(0, 30)]   // defensive: never index below the first entry
    public void Default_profile_waits_30s_2m_10m_30m_1h(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DeliveryDispatcher.DelayAfter(attempt, "default"));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DeliveryDispatcher.DelayAfter(attempt, null));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void Demo_profile_retries_fast_enough_to_watch(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DeliveryDispatcher.DelayAfter(attempt, "demo"));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DeliveryDispatcher.DelayAfter(attempt, "DEMO"));
    }

    [Fact]
    public void Queue_messages_round_trip_through_json()
    {
        var incoming = new IncomingMessage("abc123", "website-contact", "form-1", "{\"name\":\"Anna\"}", DateTimeOffset.Parse("2026-10-13T10:00:00Z"), "corr", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        var delivery = new DeliveryMessage(42, 1, "corr", null, AfterAttempt: 2, DeliveryReasons.Retry);

        var json = QueueJson.Serialize(incoming);
        Assert.Contains("\"keyId\":\"abc123\"", json); // camelCase on the wire
        Assert.Equal(incoming, QueueJson.Deserialize<IncomingMessage>(json));
        Assert.Equal(delivery, QueueJson.Deserialize<DeliveryMessage>(QueueJson.Serialize(delivery)));
    }
}
