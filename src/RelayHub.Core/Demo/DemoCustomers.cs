using System.Security.Cryptography;
using System.Text;

namespace RelayHub.Core.Demo;

/// <summary>
/// A fake customer of the demo studio. The provider ids are stable per name, so every simulated message from "Anna Nguyen"
/// lands in the same conversation whatever the channel.
/// </summary>
/// <param name="MetaId">Page-scoped / Instagram-scoped sender id (PSID / IGSID), a numeric string like Meta's.</param>
/// <param name="TelegramId">Telegram user id (= private chat id).</param>
public sealed record DemoCustomer(string Name, string FirstName, string LastName, string Handle, string Email, string MetaId, long TelegramId);

public static class DemoCustomers
{
    /// <summary>The five regulars of the demo studio.</summary>
    public static readonly IReadOnlyList<DemoCustomer> Pool =
    [
        new("Anna Nguyen", "Anna", "Nguyen", "anna.bakes", "anna@annabakes.example", "24010000000000001", 700000001),
        new("Marco Rossi", "Marco", "Rossi", "marco.rossi", "marco@trattoriarossi.example", "24010000000000002", 700000002),
        new("Linh Tran", "Linh", "Tran", "linhtran", "linh.tran@example.com", "24010000000000003", 700000003),
        new("Jamal Carter", "Jamal", "Carter", "jamal.carter", "jamal@carterfitness.example", "24010000000000004", 700000004),
        new("Sofia Alvarez", "Sofia", "Alvarez", "sofia.alvarez", "sofia@alvarezdental.example", "24010000000000005", 700000005),
    ];

    /// <summary>
    /// No name → a random regular. A regular's name or handle → that regular. Any other name → a customer whose ids are
    /// derived from the name (SHA-256), so the same visitor-typed name always maps to the same conversation.
    /// </summary>
    public static DemoCustomer Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Pool[Random.Shared.Next(Pool.Count)];

        var trimmed = Normalize(name);
        var regular = Pool.FirstOrDefault(c =>
            string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Handle, trimmed.TrimStart('@'), StringComparison.OrdinalIgnoreCase));
        return regular ?? FromName(trimmed);
    }

    /// <summary>Deterministic customer for an arbitrary display name.</summary>
    public static DemoCustomer FromName(string name)
    {
        name = Normalize(name);
        if (name.Length == 0) throw new ArgumentException("Name must not be empty", nameof(name));

        var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var first = parts[0];
        var last = parts.Length > 1 ? parts[1] : "";

        var handleChars = name.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '.').ToArray();
        var handle = string.Join('.', new string(handleChars).Split('.', StringSplitOptions.RemoveEmptyEntries));
        if (handle.Length == 0) handle = "visitor";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant()));
        var n1 = BitConverter.ToUInt64(hash, 0);
        var n2 = BitConverter.ToUInt64(hash, 8);
        var metaId = "2409" + (n1 % 10_000_000_000_000UL).ToString("D13");   // 17 digits like a real PSID
        var telegramId = 800_000_000L + (long)(n2 % 100_000_000UL);

        return new DemoCustomer(name, first, last, handle, $"{handle}@example.com", metaId, telegramId);
    }

    private static string Normalize(string name) => string.Join(' ', name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
