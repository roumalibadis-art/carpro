using Prospecta.Application.Prospecting;

namespace Prospecta.Web.Security;

/// <summary>Hourly follow-up reminder run (idempotent per follow-up and day). Disabled with Notifications:Enabled=false.</summary>
public sealed class ReminderHostedService(IServiceScopeFactory scopes, IConfiguration config, ILogger<ReminderHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Notifications:Enabled", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var created = await scope.ServiceProvider.GetRequiredService<FollowUpReminderService>().RunAsync(stoppingToken);
                if (created > 0) log.LogInformation("{Count} rappel(s) de relance créé(s)", created);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Échec de la génération des rappels de relance");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
