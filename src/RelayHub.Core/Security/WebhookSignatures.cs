using System.Security.Cryptography;
using System.Text;

namespace RelayHub.Core.Security;

/// <summary>
/// HMAC-SHA256 signatures of a raw request body, as sent by Meta (<c>X-Hub-Signature-256</c>) and by our own Cloudflare Email
/// Worker (<c>X-RelayHub-Signature</c>): header value <c>sha256=&lt;lowercase hex&gt;</c>. Verified without any database access,
/// compared in constant time. The body must be the exact bytes received: re-serialised JSON would change the hash.
/// </summary>
public static class WebhookSignatures
{
    public const string Prefix = "sha256=";

    public static string Sign(ReadOnlySpan<byte> body, string secret)
        => Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>True when <paramref name="header"/> is <c>sha256=HMAC(secret, body)</c>. False for a missing / malformed header or an empty secret.</summary>
    public static bool IsValid(string? header, string? secret, ReadOnlySpan<byte> body)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(secret)) return false;
        header = header.Trim();
        if (!header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var expected = Encoding.ASCII.GetBytes(Sign(body, secret));
        var actual = Encoding.ASCII.GetBytes(header.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Constant-time comparison of two shared secrets (Meta verify token, Telegram secret token). False when either is empty.</summary>
    public static bool SecretEquals(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>Stable id for a payload that carries no provider id of its own (a whole Meta batch): first 32 hex chars of SHA-256(body).</summary>
    public static string BodyHashId(ReadOnlySpan<byte> body) => Convert.ToHexStringLower(SHA256.HashData(body))[..32];
}
