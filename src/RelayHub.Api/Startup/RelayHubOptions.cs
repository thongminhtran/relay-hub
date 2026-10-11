namespace RelayHub.Api.Startup;

public sealed class RelayHubOptions
{
    public const string SectionName = "RelayHub";

    /// <summary>Master secret for signing source API keys. Set via user-secrets / environment / Key Vault, never in appsettings.</summary>
    public string ApiKeyMasterKey { get; set; } = "";

    /// <summary>Apply EF Core migrations when the API starts (local development only; CI/CD applies a migration bundle).</summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>Create the Studio workspace, a website-contact source and (optionally) a Discord route on startup.</summary>
    public bool SeedOnStartup { get; set; }

    /// <summary>Optional: Discord incoming-webhook URL used by the seeder to create the first destination + route.</summary>
    public string? SeedDiscordWebhookUrl { get; set; }
}
