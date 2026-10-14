using Microsoft.EntityFrameworkCore;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;

namespace RelayHub.Infrastructure.Data;

public sealed class RelayHubDbContext(DbContextOptions<RelayHubDbContext> options, ICurrentWorkspace workspace)
    : DbContext(options)
{
    private readonly ICurrentWorkspace _workspace = workspace;

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Destination> Destinations => Set<Destination>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Workspace>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.RetryProfile).HasMaxLength(50);
        });

        // No workspace filter: login looks a user up by email before any workspace is known; the token then scopes everything else.
        b.Entity<User>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.PasswordHash).HasMaxLength(500);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<Source>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.KeyId).HasMaxLength(32);
            e.Property(x => x.ExternalAccountId).HasMaxLength(200);
            e.HasIndex(x => x.KeyId).IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.Name }).IsUnique();
            // Provider webhooks (Meta, email) are matched to a source by account id; unique so a Page can only be connected once.
            e.HasIndex(x => new { x.Type, x.ExternalAccountId }).IsUnique().HasFilter("[ExternalAccountId] IS NOT NULL");
            e.HasQueryFilter(x => _workspace.WorkspaceId == null || x.WorkspaceId == _workspace.WorkspaceId);
        });

        b.Entity<Conversation>(e =>
        {
            e.Property(x => x.ExternalThreadId).HasMaxLength(200);
            e.Property(x => x.SenderName).HasMaxLength(200);
            e.Property(x => x.SenderHandle).HasMaxLength(200);
            e.HasIndex(x => new { x.SourceId, x.ExternalThreadId }).IsUnique();
            e.HasQueryFilter(x => _workspace.WorkspaceId == null || x.WorkspaceId == _workspace.WorkspaceId);
        });

        b.Entity<Destination>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasQueryFilter(x => _workspace.WorkspaceId == null || x.WorkspaceId == _workspace.WorkspaceId);
        });

        b.Entity<Route>(e =>
        {
            e.Property(x => x.Template).HasMaxLength(2000);
            e.Property(x => x.Filter).HasMaxLength(200);
            e.HasQueryFilter(x => _workspace.WorkspaceId == null || x.WorkspaceId == _workspace.WorkspaceId);
        });

        b.Entity<Event>(e =>
        {
            e.Property(x => x.ExternalId).HasMaxLength(200);
            e.Property(x => x.Channel).HasMaxLength(50);
            e.Property(x => x.SenderName).HasMaxLength(200);
            e.Property(x => x.SenderHandle).HasMaxLength(200);
            e.Property(x => x.Text).HasMaxLength(4000);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.ProviderMessageId).HasMaxLength(200);
            // Idempotency: the same provider message / Idempotency-Key can only be stored once per source.
            e.HasIndex(x => new { x.SourceId, x.ExternalId }).IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.ReceivedAt });
            e.HasMany(x => x.Deliveries).WithOne().HasForeignKey(x => x.EventId);
            e.HasQueryFilter(x => _workspace.WorkspaceId == null || x.WorkspaceId == _workspace.WorkspaceId);
        });

        b.Entity<Delivery>(e =>
        {
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasMany(x => x.Attempts).WithOne().HasForeignKey(x => x.DeliveryId);
        });

        b.Entity<DeliveryAttempt>(e =>
        {
            e.Property(x => x.Error).HasMaxLength(1000);
        });
    }
}
