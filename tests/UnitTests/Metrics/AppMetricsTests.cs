using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using UrlShortener.Analytics;
using UrlShortener.Caching;
using UrlShortener.Configurations;
using UrlShortener.DTOs;
using UrlShortener.Entities;
using UrlShortener.Metrics;
using UrlShortener.Repositories;
using UrlShortener.Services;

namespace UrlShortener.UnitTests.Metrics;

/// <summary>
/// The metrics are process-wide static counters and test classes run in parallel, so every test
/// compares a counter before and after its own action and asserts "at least" the expected increase.
/// </summary>
public class AppMetricsTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    // ---- cache hit / miss ----

    private static CachedUrlShortenerService CreateCache(Mock<IUrlShortenerService> inner)
    {
        var clock = new FixedClock(Now);
        var options = Options.Create(new CacheOptions());
        IDistributedCache cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        return new CachedUrlShortenerService(
            inner.Object, cache, new CacheHealth(clock, options), clock, options,
            NullLogger<CachedUrlShortenerService>.Instance);
    }

    [Fact]
    public async Task CachedResolve_MissThenHit_IncrementsBothCounters()
    {
        var inner = new Mock<IUrlShortenerService>();
        inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedLink(1, "https://example.com", Now.UtcDateTime.AddDays(1)));
        var sut = CreateCache(inner);

        var missBefore = AppMetrics.CacheLookups.WithLabels("miss").Value;
        var hitBefore = AppMetrics.CacheLookups.WithLabels("hit").Value;

        await sut.ResolveAsync("100001", CancellationToken.None); // miss, then stored
        await sut.ResolveAsync("100001", CancellationToken.None); // hit

        AppMetrics.CacheLookups.WithLabels("miss").Value.Should().BeGreaterThanOrEqualTo(missBefore + 1);
        AppMetrics.CacheLookups.WithLabels("hit").Value.Should().BeGreaterThanOrEqualTo(hitBefore + 1);
    }

    // ---- clicks enqueued / dropped ----

    private static ClickTracker CreateTracker(bool queueAccepts)
    {
        var queue = new Mock<IClickQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<ClickRecord>())).Returns(queueAccepts);
        return new ClickTracker(
            queue.Object,
            Mock.Of<ICountryResolver>(),
            new FixedClock(Now),
            Options.Create(new AnalyticsOptions { Salt = "salt" }),
            NullLogger<ClickTracker>.Instance);
    }

    [Fact]
    public void Track_QueueAccepts_IncrementsEnqueued()
    {
        var before = AppMetrics.ClicksEnqueued.Value;

        CreateTracker(queueAccepts: true).Track(new DefaultHttpContext(), shortUrlId: 1);

        AppMetrics.ClicksEnqueued.Value.Should().BeGreaterThanOrEqualTo(before + 1);
    }

    [Fact]
    public void Track_QueueFull_IncrementsDropped()
    {
        var before = AppMetrics.ClicksDropped.Value;

        CreateTracker(queueAccepts: false).Track(new DefaultHttpContext(), shortUrlId: 1);

        AppMetrics.ClicksDropped.Value.Should().BeGreaterThanOrEqualTo(before + 1);
    }

    // ---- links created: counted for a new link, a duplicate must not call Add ----

    private static UrlShortenerService CreateService(Mock<IShortUrlRepository> repo) =>
        new(repo.Object,
            Options.Create(new ShortenerOptions { BaseUrl = "http://localhost:8080/", DefaultExpiryDays = 30, MaxExpiryDays = 365 }),
            new FixedClock(Now),
            NullLogger<UrlShortenerService>.Instance);

    [Fact]
    public async Task Create_NewLink_IncrementsLinksCreated()
    {
        var repo = new Mock<IShortUrlRepository>();
        repo.Setup(r => r.FindActiveByHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ShortUrl?)null);
        repo.Setup(r => r.AddAsync(It.IsAny<ShortUrl>(), It.IsAny<Func<long, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ShortUrl e, Func<long, string> codeFromId, CancellationToken _) =>
            {
                e.Id = 1;
                e.Code = codeFromId(1);
                return e;
            });
        var before = AppMetrics.LinksCreated.Value;

        await CreateService(repo).CreateAsync(new CreateShortUrlRequest("https://example.com/a"), CancellationToken.None);

        AppMetrics.LinksCreated.Value.Should().BeGreaterThanOrEqualTo(before + 1);
    }

    [Fact]
    public async Task Create_Duplicate_DoesNotInsertAgain()
    {
        var existing = new ShortUrl
        {
            Id = 5,
            Code = "100005",
            OriginalUrl = "https://example.com/a",
            ExpiresAt = Now.UtcDateTime.AddDays(10),
            CreatedAt = Now.UtcDateTime
        };
        var repo = new Mock<IShortUrlRepository>();
        repo.Setup(r => r.FindActiveByHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var result = await CreateService(repo).CreateAsync(new CreateShortUrlRequest("https://example.com/a"), CancellationToken.None);

        result.Created.Should().BeFalse();
        repo.Verify(r => r.AddAsync(It.IsAny<ShortUrl>(), It.IsAny<Func<long, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- expired links deleted ----

    [Fact]
    public async Task Cleanup_DeletedLinks_AreAddedToCounter()
    {
        var repo = new Mock<IShortUrlRepository>();
        repo.Setup(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);
        var cleaner = new ExpiredLinkCleaner(
            repo.Object, new FixedClock(Now),
            Options.Create(new CleanupOptions { BatchSize = 1000, GracePeriodDays = 7 }),
            NullLogger<ExpiredLinkCleaner>.Instance);
        var before = AppMetrics.ExpiredLinksDeleted.Value;

        await cleaner.RunOnceAsync(CancellationToken.None);

        AppMetrics.ExpiredLinksDeleted.Value.Should().BeGreaterThanOrEqualTo(before + 4);
    }

    // ---- database retries ----

    [Fact]
    public async Task Retry_IncrementsDatabaseRetries()
    {
        var inner = new Mock<IShortUrlRepository>();
        inner.SetupSequence(r => r.GetByCodeAsync("x", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException())
            .ReturnsAsync(new ShortUrl());
        var sut = new ResilientShortUrlRepository(
            inner.Object,
            ResilientShortUrlRepository.CreatePipeline(
                new ResilienceOptions { MaxRetryAttempts = 2, BaseDelayMilliseconds = 1 },
                NullLogger.Instance));
        var before = AppMetrics.DatabaseRetries.Value;

        await sut.GetByCodeAsync("x", CancellationToken.None);

        AppMetrics.DatabaseRetries.Value.Should().BeGreaterThanOrEqualTo(before + 1);
    }
}
