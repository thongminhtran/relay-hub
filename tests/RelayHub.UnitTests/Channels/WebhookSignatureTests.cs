using System.Text;
using RelayHub.Core.Security;

namespace RelayHub.UnitTests.Channels;

public class WebhookSignatureTests
{
    private const string Secret = "meta-app-secret-for-tests";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"object":"page","entry":[{"id":"1","messaging":[]}]}""");

    [Fact]
    public void Valid_signature_over_the_raw_body_is_accepted()
    {
        var header = WebhookSignatures.Sign(Body, Secret);

        Assert.StartsWith("sha256=", header);
        Assert.True(WebhookSignatures.IsValid(header, Secret, Body));
        Assert.True(WebhookSignatures.IsValid(header.ToUpperInvariant(), Secret, Body), "hex case must not matter");
        Assert.True(WebhookSignatures.IsValid(" " + header + " ", Secret, Body), "surrounding whitespace is tolerated");
    }

    [Fact]
    public void Tampered_body_is_rejected()
    {
        var header = WebhookSignatures.Sign(Body, Secret);
        var tampered = Encoding.UTF8.GetBytes("""{"object":"page","entry":[{"id":"2","messaging":[]}]}""");

        Assert.False(WebhookSignatures.IsValid(header, Secret, tampered));
        Assert.False(WebhookSignatures.IsValid(header, "another-secret", Body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("sha1=abcdef")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    public void Missing_or_malformed_header_is_rejected(string? header)
    {
        Assert.False(WebhookSignatures.IsValid(header, Secret, Body));
    }

    [Fact]
    public void Empty_secret_never_validates()
    {
        var header = WebhookSignatures.Sign(Body, "");
        Assert.False(WebhookSignatures.IsValid(header, "", Body));
        Assert.False(WebhookSignatures.IsValid(header, null, Body));
    }

    [Fact]
    public void Shared_secret_compare_is_strict()
    {
        Assert.True(WebhookSignatures.SecretEquals("verify-me", "verify-me"));
        Assert.False(WebhookSignatures.SecretEquals("verify-me", "verify-mE"));
        Assert.False(WebhookSignatures.SecretEquals("", ""));
        Assert.False(WebhookSignatures.SecretEquals(null, "x"));
    }

    [Fact]
    public void Body_hash_id_is_stable_and_32_hex_chars()
    {
        var a = WebhookSignatures.BodyHashId(Body);
        Assert.Equal(32, a.Length);
        Assert.Equal(a, WebhookSignatures.BodyHashId(Body));
        Assert.NotEqual(a, WebhookSignatures.BodyHashId(Encoding.UTF8.GetBytes("{}")));
    }
}
