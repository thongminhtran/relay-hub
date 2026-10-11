using System.Security.Cryptography;
using System.Text;

namespace RelayHub.Core.Security;

/// <summary>
/// Stateless signed API keys: <c>src_{keyId}_{signature}</c> where signature = HMAC-SHA256(masterKey, keyId).
/// The API can verify a key without a database lookup; the key id then identifies the source.
/// </summary>
public static class ApiKeys
{
    private const string Prefix = "src_";
    private const int SignatureHexLength = 32; // 128 bits is plenty for a key that is also looked up by id

    public static string NewKeyId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)); // 12 hex chars

    public static string Create(string keyId, string masterKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKey);
        return $"{Prefix}{keyId}_{Sign(keyId, masterKey)}";
    }

    public static bool TryVerify(string? apiKey, string masterKey, out string keyId)
    {
        keyId = "";
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(masterKey)) return false;
        if (!apiKey.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var parts = apiKey[Prefix.Length..].Split('_', 2);
        if (parts.Length != 2 || parts[0].Length == 0) return false;

        var expected = Encoding.ASCII.GetBytes(Sign(parts[0], masterKey));
        var actual = Encoding.ASCII.GetBytes(parts[1]);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return false;

        keyId = parts[0];
        return true;
    }

    private static string Sign(string keyId, string masterKey)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(masterKey), Encoding.UTF8.GetBytes(keyId));
        return Convert.ToHexStringLower(mac)[..SignatureHexLength];
    }
}
