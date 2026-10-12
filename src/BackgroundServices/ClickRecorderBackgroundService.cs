using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UrlShortener.Analytics;
using UrlShortener.Configurations;
using UrlShortener.Entities;
using UrlShortener.Repositories;

namespace UrlShortener.BackgroundServices;

/// <summary>
/// Takes clicks off the in-memory queue and writes them to ClickEvents in batches
/// (up to BatchSize clicks, or whatever arrived within FlushIntervalSeconds).
/// A fresh DI scope (and DbContext) is used per batch because DbContext is not thread-safe
/// and must not live for the whole lifetime of a singleton.
/// </summary>
public sealed class ClickRecorderBackgroundService : BackgroundService
{
    private readonly IClickQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClickRecorderBackgroundService> _logger;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;

    public ClickRecorderBackgroundService(
        IClickQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<AnalyticsOptions> options,
        ILogger<ClickRecorderBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _batchSize = Math.Max(1, options.Value.BatchSize);
        _flushInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.FlushIntervalSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _queue.Reader;
        var batch = new List<ClickRecord>(_batchSize);

        try
        {
            // Wait for the first click, then keep collecting until the batch is full or the window closes.
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                window.CancelAfter(_flushInterval);

                try
                {
                    while (batch.Count < _batchSize && await reader.WaitToReadAsync(window.Token))
                    {
                        while (batch.Count < _batchSize && reader.TryRead(out var click))
                        {
                            batch.Add(click);
                        }
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The flush window elapsed. Write what we have.
                }

                await FlushAsync(batch, CancellationToken.None);
                batch.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }

        // Shutdown: write everything still in memory so a normal restart loses nothing.
        // (A crash or kill -9 still loses whatever is queued: that is the documented trade-off.)
        while (_queue.Reader.TryRead(out var remaining))
        {
            batch.Add(remaining);
            if (batch.Count >= _batchSize)
            {
                await FlushAsync(batch, CancellationToken.None);
                batch.Clear();
            }
        }

        await FlushAsync(batch, CancellationToken.None);
    }

    private async Task FlushAsync(List<ClickRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await InsertAsync(batch, ct);
            _logger.LogDebug("Stored {Count} click(s)", batch.Count);
        }
        catch (Exception ex)
        {
            // Most likely cause: a link was deleted after its click was queued (foreign key violation),
            // which would otherwise reject the whole batch. Retry one by one so a single bad click
            // cannot discard its neighbours.
            _logger.LogWarning(ex, "Batch insert of {Count} click(s) failed; retrying individually", batch.Count);

            foreach (var click in batch)
            {
                try
                {
                    await InsertAsync(new[] { click }, ct);
                }
                catch (Exception single)
                {
                    _logger.LogError(single, "Dropped click for link {ShortUrlId}", click.ShortUrlId);
                }
            }
        }
    }

    private async Task InsertAsync(IEnumerable<ClickRecord> clicks, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.ClickEvents.AddRange(clicks.Select(c => new ClickEvent
        {
            ShortUrlId = c.ShortUrlId,
            ClickedAt = c.ClickedAt,
            VisitorHash = c.VisitorHash,
            Country = c.Country,
            Browser = c.Browser
        }));

        await db.SaveChangesAsync(ct);
    }
}
