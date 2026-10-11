using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using RelayHub.Core.Abstractions;

namespace RelayHub.Infrastructure.Data;

/// <summary>Used only by <c>dotnet ef</c> at design time (migrations). No database connection is opened.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RelayHubDbContext>
{
    public RelayHubDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RelayHubDbContext>()
            .UseSqlServer("Server=localhost;Database=RelayHub;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new RelayHubDbContext(options, new NoWorkspace());
    }

    private sealed class NoWorkspace : ICurrentWorkspace
    {
        public int? WorkspaceId => null;
    }
}
