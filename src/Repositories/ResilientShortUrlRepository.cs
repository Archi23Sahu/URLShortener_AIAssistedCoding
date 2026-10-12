using Microsoft.Data.SqlClient;
using Polly;
using Polly.Retry;
using UrlShortener.Configurations;
using UrlShortener.Entities;
using UrlShortener.Metrics;

namespace UrlShortener.Repositories;

/// <summary>
/// Polly retry decorator over the repository. Only operations that are safe to repeat are retried:
/// reads, report queries, and set-based deletes (deleting twice is the same as deleting once).
/// AddAsync and SaveChangesAsync are NOT retried: they work on tracked entities and a transaction,
/// and a blind retry could duplicate a row or hide an optimistic-lock conflict.
/// </summary>
public sealed class ResilientShortUrlRepository : IShortUrlRepository
{
    private readonly IShortUrlRepository _inner;
    private readonly ResiliencePipeline _pipeline;

    public ResilientShortUrlRepository(IShortUrlRepository inner, ResiliencePipeline pipeline)
    {
        _inner = inner;
        _pipeline = pipeline;
    }

    /// <summary>Builds the retry pipeline. <paramref name="isTransient"/> defaults to SQL Server transient errors.</summary>
    public static ResiliencePipeline CreatePipeline(
        ResilienceOptions options,
        ILogger logger,
        Func<Exception, bool>? isTransient = null)
    {
        isTransient ??= IsTransientSqlFailure;

        if (!options.Enabled || options.MaxRetryAttempts <= 0)
        {
            return ResiliencePipeline.Empty;
        }

        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(Math.Max(0, options.BaseDelayMilliseconds)),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => isTransient(ex)),
                OnRetry = args =>
                {
                    AppMetrics.DatabaseRetries.Inc();
                    logger.LogWarning(args.Outcome.Exception,
                        "Transient database failure; retry {Attempt} in {Delay} ms",
                        args.AttemptNumber + 1, (int)args.RetryDelay.TotalMilliseconds);
                    return default;
                }
            })
            .Build();
    }

    // Error numbers Microsoft documents as transient for SQL Server / Azure SQL:
    // -2 timeout, 64 dropped connection, 1205 deadlock victim, 4060/40197/40501/40613 service busy,
    // 10928/10929 resource limits, 49918-49920 cannot process request.
    private static readonly HashSet<int> TransientSqlNumbers =
        [-2, 64, 1205, 4060, 10928, 10929, 40197, 40501, 40613, 49918, 49919, 49920];

    public static bool IsTransientSqlFailure(Exception ex) =>
        ex switch
        {
            SqlException sql => sql.Errors.Cast<SqlError>().Any(e => TransientSqlNumbers.Contains(e.Number)),
            TimeoutException => true,
            _ => false
        };

    // ---- retried (idempotent) ----

    public async Task<ShortUrl?> GetByCodeAsync(string code, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(async t => await _inner.GetByCodeAsync(code, t), ct);

    public async Task<ShortUrl?> GetByCodeForUpdateAsync(string code, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(async t => await _inner.GetByCodeForUpdateAsync(code, t), ct);

    public async Task<ShortUrl?> FindActiveByHashAsync(string hash, string normalizedUrl, DateTime nowUtc, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(async t => await _inner.FindActiveByHashAsync(hash, normalizedUrl, nowUtc, t), ct);

    public async Task<bool> DeleteByCodeAsync(string code, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(async t => await _inner.DeleteByCodeAsync(code, t), ct);

    public async Task<ClickStats> GetClickStatsAsync(long shortUrlId, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(async t => await _inner.GetClickStatsAsync(shortUrlId, t), ct);

    public async Task<IReadOnlyList<DailyClickRow>> GetDailyClicksAsync(
        long shortUrlId, DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(async t => await _inner.GetDailyClicksAsync(shortUrlId, fromUtc, toExclusiveUtc, t), ct);

    public async Task<IReadOnlyList<BreakdownRow>> GetCountryBreakdownAsync(
        long shortUrlId, DateTime? fromUtc, DateTime? toExclusiveUtc, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(async t => await _inner.GetCountryBreakdownAsync(shortUrlId, fromUtc, toExclusiveUtc, t), ct);

    public async Task<IReadOnlyList<BreakdownRow>> GetBrowserBreakdownAsync(
        long shortUrlId, DateTime? fromUtc, DateTime? toExclusiveUtc, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(async t => await _inner.GetBrowserBreakdownAsync(shortUrlId, fromUtc, toExclusiveUtc, t), ct);

    public async Task<int> DeleteExpiredAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(async t => await _inner.DeleteExpiredAsync(cutoffUtc, batchSize, t), ct);

    // ---- not retried (see class summary) ----

    public Task<ShortUrl> AddAsync(ShortUrl entity, Func<long, string> codeFromId, CancellationToken ct) =>
        _inner.AddAsync(entity, codeFromId, ct);

    public Task SaveChangesAsync(CancellationToken ct) => _inner.SaveChangesAsync(ct);
}
