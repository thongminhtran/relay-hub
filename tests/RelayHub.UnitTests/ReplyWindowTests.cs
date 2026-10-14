using RelayHub.Core.Conversations;
using RelayHub.Core.Domain;

namespace RelayHub.UnitTests;

public class ReplyWindowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T12:00:00Z");

    [Theory]
    [InlineData(SourceType.Instagram)]
    [InlineData(SourceType.Facebook)]
    public void Meta_channels_can_reply_within_24_hours_of_the_last_inbound_message(SourceType type)
    {
        var lastInbound = Now.AddHours(-23).AddMinutes(-59);

        var status = ReplyWindow.Evaluate(type, lastInbound, "@customer", Now);

        Assert.True(status.CanReply);
        Assert.Equal(lastInbound.AddHours(24), status.WindowClosesAt);
        Assert.Null(status.Reason);
    }

    [Theory]
    [InlineData(SourceType.Instagram)]
    [InlineData(SourceType.Facebook)]
    public void Meta_channels_are_closed_after_24_hours_and_still_report_when_the_window_closed(SourceType type)
    {
        var lastInbound = Now.AddHours(-25);

        var status = ReplyWindow.Evaluate(type, lastInbound, "@customer", Now);

        Assert.False(status.CanReply);
        Assert.Equal(lastInbound.AddHours(24), status.WindowClosesAt);
        Assert.Equal(ReplyWindow.MetaWindowClosedReason, status.Reason);
    }

    [Fact]
    public void Meta_window_is_exclusive_at_the_boundary()
    {
        var exactly24h = Now.AddHours(-24);
        Assert.False(ReplyWindow.Evaluate(SourceType.Instagram, exactly24h, null, Now).CanReply);
        Assert.True(ReplyWindow.Evaluate(SourceType.Instagram, exactly24h.AddSeconds(1), null, Now).CanReply);
    }

    [Fact]
    public void Meta_conversation_without_an_inbound_message_cannot_be_replied_to()
    {
        var status = ReplyWindow.Evaluate(SourceType.Facebook, null, "@customer", Now);
        Assert.False(status.CanReply);
        Assert.Null(status.WindowClosesAt);
    }

    [Theory]
    [InlineData(SourceType.Telegram)]
    [InlineData(SourceType.Email)]
    public void Telegram_and_email_have_no_window(SourceType type)
    {
        var status = ReplyWindow.Evaluate(type, Now.AddDays(-30), null, Now);

        Assert.True(status.CanReply);
        Assert.Null(status.WindowClosesAt);
        Assert.Null(status.Reason);
    }

    [Fact]
    public void Web_form_replies_only_when_the_visitor_left_an_email()
    {
        Assert.True(ReplyWindow.Evaluate(SourceType.Generic, Now.AddDays(-3), "anna@example.com", Now).CanReply);

        var noEmail = ReplyWindow.Evaluate(SourceType.Generic, Now, "Anna", Now);
        Assert.False(noEmail.CanReply);
        Assert.Null(noEmail.WindowClosesAt);
        Assert.Equal(ReplyWindow.NoReplyChannelReason, noEmail.Reason);

        Assert.False(ReplyWindow.Evaluate(SourceType.Generic, Now, null, Now).CanReply);
    }
}
