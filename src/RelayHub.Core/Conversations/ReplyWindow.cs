using RelayHub.Core.Domain;

namespace RelayHub.Core.Conversations;

/// <summary>Whether the owner may reply to a conversation right now, and why not. Pure rule, no I/O (unit-tested).</summary>
/// <param name="CanReply">True when a reply can be attempted.</param>
/// <param name="WindowClosesAt">Meta channels only: the end of the 24-hour window after the customer's last message (set even when already closed).</param>
/// <param name="Reason">Why <see cref="CanReply"/> is false; null when it is true.</param>
public sealed record ReplyWindowStatus(bool CanReply, DateTimeOffset? WindowClosesAt, string? Reason);

/// <summary>
/// Reply rules per channel (plan decision: the Meta 24-hour window is enforced from Conversation.LastInboundAt before the Send API is called).
/// Instagram / Facebook: within 24 h of the customer's last message. Telegram / email: always. Web (generic): only when the form left an
/// email address (SenderHandle contains "@"), because the reply goes out as email.
/// </summary>
public static class ReplyWindow
{
    public static readonly TimeSpan MetaWindow = TimeSpan.FromHours(24);

    public const string MetaWindowClosedReason = "Meta allows replies only within 24 hours of the customer's last message";
    public const string NoReplyChannelReason = "This source has no reply channel";

    public static ReplyWindowStatus Evaluate(SourceType sourceType, DateTimeOffset? lastInboundAt, string? senderHandle, DateTimeOffset now)
    {
        switch (sourceType)
        {
            case SourceType.Instagram:
            case SourceType.Facebook:
            {
                if (lastInboundAt is not { } inbound) return new(false, null, MetaWindowClosedReason);
                var closesAt = inbound + MetaWindow;
                return now < closesAt ? new(true, closesAt, null) : new(false, closesAt, MetaWindowClosedReason);
            }
            case SourceType.Telegram:
            case SourceType.Email:
                return new(true, null, null);
            default:
                return senderHandle is not null && senderHandle.Contains('@')
                    ? new(true, null, null)
                    : new(false, null, NoReplyChannelReason);
        }
    }
}
