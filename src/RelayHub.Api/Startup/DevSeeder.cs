using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelayHub.Api.Endpoints;
using RelayHub.Core.Domain;
using RelayHub.Core.Security;
using RelayHub.Infrastructure;
using RelayHub.Infrastructure.Data;

namespace RelayHub.Api.Startup;

/// <summary>Startup helper: migrates the database (dev) and creates the first workspaces, the owner + demo logins, a source and an optional Discord route.</summary>
public static class DevSeeder
{
    private const string DevMasterKey = "dev-only-master-key-change-me";
    public const string DevOwnerEmail = "owner@relayhub.local";
    public const string DevOwnerPassword = "relayhub-dev-owner";

    public static async Task RunAsync(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<RelayHubOptions>>().Value;
        var log = app.Logger;

        if (string.IsNullOrWhiteSpace(options.ApiKeyMasterKey))
        {
            if (!app.Environment.IsDevelopment())
                throw new InvalidOperationException("RelayHub:ApiKeyMasterKey must be configured outside Development.");
            options.ApiKeyMasterKey = DevMasterKey;
            log.LogWarning("RelayHub:ApiKeyMasterKey is not set; using the built-in development key.");
        }

        if (string.IsNullOrWhiteSpace(options.OwnerEmail) || string.IsNullOrWhiteSpace(options.OwnerPassword))
        {
            if (!app.Environment.IsDevelopment())
                throw new InvalidOperationException("RelayHub:OwnerEmail and RelayHub:OwnerPassword must be configured outside Development.");
            options.OwnerEmail = DevOwnerEmail;
            options.OwnerPassword = DevOwnerPassword;
            log.LogWarning("RelayHub:OwnerEmail / OwnerPassword are not set; using the built-in development owner {Email}.", DevOwnerEmail);
        }

        if (!options.MigrateOnStartup && !options.SeedOnStartup) return;

        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AmbientWorkspace>().WorkspaceId = null; // unscoped
        var db = scope.ServiceProvider.GetRequiredService<RelayHubDbContext>();

        if (options.MigrateOnStartup)
        {
            log.LogInformation("Applying EF Core migrations...");
            await db.Database.MigrateAsync();
        }

        if (!options.SeedOnStartup) return;

        var studio = await db.Workspaces.FirstOrDefaultAsync(w => w.Name == "Studio");
        if (studio is null)
        {
            studio = new Workspace { Name = "Studio" };
            db.Workspaces.Add(studio);
            db.Workspaces.Add(new Workspace { Name = "Demo", IsDemo = true, RetryProfile = "demo" });
            await db.SaveChangesAsync();
        }

        await SeedUsersAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(), options, studio, log);

        var source = await db.Sources.FirstOrDefaultAsync(s => s.WorkspaceId == studio.Id && s.Name == "website-contact");
        if (source is null)
        {
            source = new Source { WorkspaceId = studio.Id, Type = SourceType.Generic, Name = "website-contact", KeyId = ApiKeys.NewKeyId() };
            db.Sources.Add(source);
            await db.SaveChangesAsync();
        }

        // The key is derived from the master key, so it can always be re-issued; it is never stored.
        if (app.Environment.IsDevelopment())
            log.LogInformation("Source '{Source}' API key (send as X-Api-Key): {ApiKey}", source.Name, ApiKeys.Create(source.KeyId, options.ApiKeyMasterKey));
        else
            log.LogInformation("Source '{Source}' ready (id {Id}); call POST /api/v1/sources/{Id}/rotate-key to obtain its API key.", source.Name, source.Id, source.Id);

        if (!string.IsNullOrWhiteSpace(options.SeedDiscordWebhookUrl)
            && !await db.Destinations.AnyAsync(d => d.WorkspaceId == studio.Id && d.Type == DestinationType.Discord))
        {
            var discord = new Destination
            {
                WorkspaceId = studio.Id,
                Type = DestinationType.Discord,
                Name = "Discord",
                ConfigJson = System.Text.Json.JsonSerializer.Serialize(new { webhookUrl = options.SeedDiscordWebhookUrl }),
            };
            db.Destinations.Add(discord);
            await db.SaveChangesAsync();
            db.Routes.Add(new RelayHub.Core.Domain.Route
            {
                WorkspaceId = studio.Id,
                SourceId = source.Id,
                DestinationId = discord.Id,
                Template = "**New message from {{name}}** via {{channel}}\n{{text}}\n_{{email}}_",
            });
            await db.SaveChangesAsync();
            log.LogInformation("Seeded Discord destination and route for '{Source}'.", source.Name);
        }
    }

    /// <summary>
    /// Owner: created from RelayHub:OwnerEmail / OwnerPassword; when the configured password no longer matches the stored hash
    /// (rotated secret), the hash is replaced, so a redeploy is enough to change the password. Demo: a role-Demo row in the Demo
    /// workspace with an unguessable password; it only exists so demo tokens have a real subject.
    /// </summary>
    private static async Task SeedUsersAsync(RelayHubDbContext db, IPasswordHasher<User> hasher, RelayHubOptions options, Workspace studio, ILogger log)
    {
        var email = options.OwnerEmail.Trim().ToLowerInvariant();
        var owner = await db.Users.FirstOrDefaultAsync(u => u.Role == UserRole.Owner && u.WorkspaceId == studio.Id);
        if (owner is null)
        {
            owner = new User { Email = email, PasswordHash = "", Role = UserRole.Owner, WorkspaceId = studio.Id };
            owner.PasswordHash = hasher.HashPassword(owner, options.OwnerPassword);
            db.Users.Add(owner);
            log.LogInformation("Created owner user {Email}.", email);
        }
        else
        {
            if (owner.Email != email) { owner.Email = email; log.LogInformation("Owner email updated to {Email}.", email); }
            if (hasher.VerifyHashedPassword(owner, owner.PasswordHash, options.OwnerPassword) != PasswordVerificationResult.Success)
            {
                owner.PasswordHash = hasher.HashPassword(owner, options.OwnerPassword);
                log.LogInformation("Owner password hash updated from configuration.");
            }
        }
        await db.SaveChangesAsync();

        var demo = await db.Workspaces.Where(w => w.IsDemo).OrderBy(w => w.Id).FirstOrDefaultAsync();
        if (demo is not null && !await db.Users.AnyAsync(u => u.Email == AuthEndpoints.DemoUserEmail))
            await AuthEndpoints.CreateDemoUserAsync(db, hasher, demo, CancellationToken.None);
    }
}
