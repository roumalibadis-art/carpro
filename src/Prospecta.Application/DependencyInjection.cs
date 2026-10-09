using Microsoft.Extensions.DependencyInjection;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Businesses;
using Prospecta.Application.Common;
using Prospecta.Application.Dashboard;
using Prospecta.Application.Duplicates;
using Prospecta.Application.Imports;
using Prospecta.Application.Prospecting;
using Prospecta.Application.Reference;
using Prospecta.Application.Users;

namespace Prospecta.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddOptions<DuplicateOptions>().BindConfiguration("Duplicates");
        services.AddOptions<ImportOptions>().BindConfiguration("Import");
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ReferenceService>();
        services.AddScoped<DuplicateService>();
        services.AddScoped<BusinessService>();
        services.AddScoped<ImportService>();
        services.AddScoped<ExportService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<SavedFilterService>();
        services.AddScoped<FollowUpService>();
        services.AddScoped<VisitService>();
        services.AddScoped<CampaignService>();
        services.AddScoped<OutingService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<FollowUpReminderService>();
        return services;
    }
}
