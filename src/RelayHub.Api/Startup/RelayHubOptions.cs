namespace RelayHub.Api.Startup;

public sealed class RelayHubOptions
{
    public const string SectionName = "RelayHub";

    /// <summary>Master secret for signing source API keys. Set via user-secrets / environment / Key Vault, never in appsettings.</summary>
    public string ApiKeyMasterKey { get; set; } = "";

    /// <summary>HS256 key for dashboard JWTs, at least 32 characters. Development falls back to a built-in key with a warning; required elsewhere.</summary>
    public string JwtSigningKey { get; set; } = "";

    /// <summary>The owner login the seeder creates/updates on startup. Development fallback: owner@relayhub.local / relayhub-dev-owner.</summary>
    public string OwnerEmail { get; set; } = "";

    /// <summary>Owner password (Key Vault in production). Changing it re-hashes the stored password on the next startup.</summary>
    public string OwnerPassword { get; set; } = "";

    /// <summary>Allowed CORS origin(s) of the dashboard, comma-separated (e.g. https://xyz.azurestaticapps.net). Development adds http://localhost:5173.</summary>
    public string? DashboardOrigin { get; set; }

    public TurnstileOptions Turnstile { get; set; } = new();

    /// <summary>Apply EF Core migrations when the API starts (local development only; CI/CD applies a migration bundle).</summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>Create the Studio workspace, a website-contact source, the owner + demo users and (optionally) a Discord route on startup.</summary>
    public bool SeedOnStartup { get; set; }

    /// <summary>Optional: Discord incoming-webhook URL used by the seeder to create the first destination + route.</summary>
    public string? SeedDiscordWebhookUrl { get; set; }

    public sealed class TurnstileOptions
    {
        /// <summary>Cloudflare Turnstile secret for POST /api/v1/auth/demo. Unset = demo tokens are issued without a challenge.</summary>
        public string? Secret { get; set; }
    }
}
