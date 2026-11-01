using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SivaCom.Data;

/// <summary>For <c>dotnet ef migrations add</c> only. Never connects: creating
/// a migration needs the model, not a database.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CommDbContext>
{
    public CommDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<CommDbContext>()
            .UseNpgsql("Host=localhost;Database=sivacom_design")
            .UseSnakeCaseNamingConvention()
            .Options);
}
