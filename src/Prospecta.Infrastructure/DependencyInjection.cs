using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Identity;
using Prospecta.Infrastructure.Persistence;

namespace Prospecta.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Database:Provider"] ?? "MySql";
        var cs = config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default est requis (voir README / user-secrets).");

        services.AddDbContext<AppDbContext>(o =>
        {
            if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                o.UseSqlite(cs);
            }
            else
            {
                // Explicit server version: no connection needed to build the model or generate migrations.
                var version = Version.Parse(config["Database:ServerVersion"] ?? "8.0.36");
                ServerVersion sv = (config["Database:ServerType"] ?? "MySql").Equals("MariaDb", StringComparison.OrdinalIgnoreCase)
                    ? new MariaDbServerVersion(version) : new MySqlServerVersion(version);
                o.UseMySql(cs, sv); // no retrying strategy: services use explicit transactions for critical operations
            }
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddMemoryCache();
        services.AddHttpClient("overpass");
        services.AddScoped<Prospecta.Application.Collection.IOverpassClient, Prospecta.Infrastructure.Collection.OverpassClient>();
        services.AddSingleton<Prospecta.Application.Collection.IPublicPageFetcher, Prospecta.Infrastructure.Collection.SafePageFetcher>();
        services.AddSingleton<Prospecta.Application.Abstractions.IReportPdfRenderer, Prospecta.Infrastructure.Reporting.PdfReportRenderer>();
        services.AddScoped<IPermissionStore, PermissionStore>();

        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireNonAlphanumeric = true;
                o.User.RequireUniqueEmail = true;
                o.Stores.MaxLengthForKeys = 128; // keeps composite Identity keys within MySQL's index size limit
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
        return services;
    }
}
