using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RelayHub.Infrastructure.Channels;

/// <summary>
/// Secrets the provider hooks and reply channels need, bound from the <c>RelayHub</c> section (Key Vault <c>RelayHub--Meta--AppSecret</c>
/// ↔ app setting <c>RelayHub__Meta__AppSecret</c> ↔ <c>RelayHub:Meta:AppSecret</c>). Shared by the API (hooks) and the worker
/// (connectors). Everything is optional: an unset secret makes its hook answer 503 "channel not configured".
/// </summary>
public sealed class ChannelOptions
{
    public const string SectionName = "RelayHub";

    public MetaOptions Meta { get; set; } = new();
    public TelegramOptions Telegram { get; set; } = new();
    public EmailOptions Email { get; set; } = new();
    public ResendOptions Resend { get; set; } = new();

    public sealed class MetaOptions
    {
        /// <summary>Meta app secret: signs every webhook body (X-Hub-Signature-256).</summary>
        public string? AppSecret { get; set; }

        /// <summary>The string entered in the Meta app's webhook configuration; echoed back on the GET verification handshake.</summary>
        public string? VerifyToken { get; set; }
    }

    public sealed class TelegramOptions
    {
        /// <summary>The <c>secret_token</c> given to setWebhook; Telegram sends it as X-Telegram-Bot-Api-Secret-Token.</summary>
        public string? WebhookSecret { get; set; }
    }

    public sealed class EmailOptions
    {
        /// <summary>Shared with the Cloudflare Email Worker (RELAYHUB_INBOUND_SECRET), which signs each forwarded email (X-RelayHub-Signature).</summary>
        public string? InboundSecret { get; set; }
    }

    public sealed class ResendOptions
    {
        public string? ApiKey { get; set; }

        /// <summary>Default sender for replies and email destinations, e.g. <c>RelayHub &lt;inbox@yourdomain.com&gt;</c> (a domain verified in Resend).</summary>
        public string? From { get; set; }
    }

    /// <summary>Names of the channels whose secret is missing, for the one startup warning.</summary>
    public IEnumerable<string> MissingSettings()
    {
        if (string.IsNullOrWhiteSpace(Meta.AppSecret)) yield return "RelayHub:Meta:AppSecret (Meta webhook disabled)";
        if (string.IsNullOrWhiteSpace(Meta.VerifyToken)) yield return "RelayHub:Meta:VerifyToken (Meta webhook verification disabled)";
        if (string.IsNullOrWhiteSpace(Telegram.WebhookSecret)) yield return "RelayHub:Telegram:WebhookSecret (Telegram webhook disabled)";
        if (string.IsNullOrWhiteSpace(Email.InboundSecret)) yield return "RelayHub:Email:InboundSecret (inbound email disabled)";
        if (string.IsNullOrWhiteSpace(Resend.ApiKey)) yield return "RelayHub:Resend:ApiKey (email replies and email destinations disabled)";
    }
}

/// <summary>Logs one warning at startup listing the optional channel secrets that are not configured.</summary>
public sealed class ChannelOptionsStartupCheck(IOptions<ChannelOptions> options, ILogger<ChannelOptionsStartupCheck> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var missing = options.Value.MissingSettings().ToList();
        if (missing.Count > 0)
            log.LogWarning("Channel settings not configured (the matching hooks answer 503 until they are): {Missing}", string.Join("; ", missing));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
