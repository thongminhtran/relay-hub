using RelayHub.Core.Security;

namespace RelayHub.UnitTests;

public class ApiKeyTests
{
    private const string Master = "unit-test-master-key";

    [Fact]
    public void Create_then_verify_roundtrips_the_key_id()
    {
        var keyId = ApiKeys.NewKeyId();
        var apiKey = ApiKeys.Create(keyId, Master);

        Assert.StartsWith("src_", apiKey);
        Assert.True(ApiKeys.TryVerify(apiKey, Master, out var verifiedKeyId));
        Assert.Equal(keyId, verifiedKeyId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("src_")]
    [InlineData("src_abc")]
    [InlineData("nope_abc_def")]
    public void Malformed_keys_are_rejected(string? apiKey)
    {
        Assert.False(ApiKeys.TryVerify(apiKey, Master, out _));
    }

    [Fact]
    public void Tampered_signature_or_wrong_master_key_is_rejected()
    {
        var apiKey = ApiKeys.Create(ApiKeys.NewKeyId(), Master);

        var tampered = apiKey[..^1] + (apiKey[^1] == 'a' ? 'b' : 'a');
        Assert.False(ApiKeys.TryVerify(tampered, Master, out _));
        Assert.False(ApiKeys.TryVerify(apiKey, "another-master-key", out _));
    }

    [Fact]
    public void Key_id_cannot_be_swapped_onto_another_signature()
    {
        var a = ApiKeys.Create("aaaaaaaaaaaa", Master);
        var b = ApiKeys.Create("bbbbbbbbbbbb", Master);
        var frankenstein = "src_bbbbbbbbbbbb_" + a.Split('_')[2];

        Assert.NotEqual(a, b);
        Assert.False(ApiKeys.TryVerify(frankenstein, Master, out _));
    }
}
