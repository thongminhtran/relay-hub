namespace RelayHub.Core.Demo;

/// <summary>
/// Names and ids of everything the public demo workspace is made of. Shared by the seeder (creates them), the simulate
/// endpoint (addresses the payloads to them) and the tests (assert on them). Change here, nowhere else.
/// </summary>
public static class DemoCatalog
{
    public const string WorkspaceName = "Demo";

    public const string WebsiteSource = "demo-website";
    public const string InstagramSource = "demo-instagram";
    public const string InstagramAccountId = "demo-ig-17841400000000001";
    public const string FacebookSource = "demo-facebook";
    public const string FacebookPageId = "demo-page-100000000000001";
    public const string TelegramSource = "demo-telegram";
    public const string TelegramConfigJson = """{"botToken":"demo"}""";
    public const string EmailSource = "demo-email";
    public const string EmailAddress = "hello@demo.relayhub.dev";

    public const string DiscordDestination = "Discord (mock)";
    public const string DiscordDestinationConfigJson = """{"webhookUrl":"https://discord.com/api/webhooks/0/demo","username":"RelayHub demo"}""";
    public const string FlakyDestination = "Flaky endpoint (mock)";
    public const string FlakyDestinationConfigJson = """{"webhookUrl":"https://discord.com/api/webhooks/0/flaky","username":"flaky","failing":false}""";
    /// <summary>The failure switch flips <c>failing</c> on every destination of the demo workspace whose name contains this.</summary>
    public const string FlakyNameMarker = "Flaky";

    /// <summary>True for the Meta / email account ids the demo seeder assigns (they all start with "demo-"); real provider ids never do.</summary>
    public static bool IsDemoAccount(string? externalAccountId) => externalAccountId?.StartsWith("demo-", StringComparison.Ordinal) == true;

    /// <summary>Plan 9.3: at most 500 demo events; when reached, the oldest <see cref="TrimBatch"/> are removed before a new one is accepted.</summary>
    public const int MaxEvents = 500;
    public const int TrimBatch = 100;
    /// <summary>Longest text a visitor can simulate (the 10 KB cap of the plan is far above what a chat message needs).</summary>
    public const int MaxSimulatedTextLength = 500;
}

/// <summary>The channels a visitor can simulate; also the <c>Event.Channel</c> values the worker stores.</summary>
public static class DemoChannels
{
    public const string Instagram = "instagram";
    public const string Facebook = "facebook";
    public const string Telegram = "telegram";
    public const string Email = "email";
    public const string Web = "web";

    public static readonly IReadOnlyList<string> All = [Instagram, Facebook, Telegram, Email, Web];

    public static bool IsKnown(string? channel) => channel is not null && All.Contains(channel.Trim().ToLowerInvariant());
}
