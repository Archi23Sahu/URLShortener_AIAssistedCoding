using Microsoft.EntityFrameworkCore;

namespace UrlShortener.Repositories;

public static class DatabaseInitializer
{
    /// <summary>
    /// Applies pending EF Core migrations. SQL Server in Docker takes several seconds to accept
    /// connections, so retry a few times before giving up. (Day 2: replace with a Polly policy.)
    /// </summary>
    public static async Task MigrateWithRetryAsync(IServiceProvider services, ILogger logger, int maxAttempts = 10)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync();
                logger.LogInformation("Database migrations applied.");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogWarning(ex, "Database not ready (attempt {Attempt}/{Max}). Retrying in 3 seconds.", attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }
}
