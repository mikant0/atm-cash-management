using Microsoft.EntityFrameworkCore;

namespace AtmCashManagement.Api.Infrastructure;

public static class DatabaseMigrator
{
    /// <summary>
    /// Applies pending EF Core migrations before the API starts accepting requests. The database
    /// container may still be starting when the API comes up, so the first attempts are retried.
    /// </summary>
    public static async Task MigrateDatabaseAsync(this WebApplication app, int maxAttempts = 10)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseMigrator");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync();
                logger.LogInformation("Database is up to date.");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}/{Max}): {Message}", attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }
}
