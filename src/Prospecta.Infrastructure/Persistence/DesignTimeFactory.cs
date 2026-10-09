using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Prospecta.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef`: builds the MySQL model without needing a live server.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? "Server=localhost;Database=prospecta;User=u;Password=p;";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseMySql(cs, new MySqlServerVersion(new Version(8, 0, 36))).Options;
        return new AppDbContext(options);
    }
}
