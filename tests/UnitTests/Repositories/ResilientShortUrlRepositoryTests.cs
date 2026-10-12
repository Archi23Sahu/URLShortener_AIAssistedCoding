using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using UrlShortener.Configurations;
using UrlShortener.Entities;
using UrlShortener.Repositories;

namespace UrlShortener.UnitTests.Repositories;

public class ResilientShortUrlRepositoryTests
{
    private readonly Mock<IShortUrlRepository> _inner = new();

    // Treat InvalidOperationException as "transient" so the tests need no real SqlException.
    private ResilientShortUrlRepository Create(int retries = 3, bool enabled = true) =>
        new(_inner.Object,
            ResilientShortUrlRepository.CreatePipeline(
                new ResilienceOptions { Enabled = enabled, MaxRetryAttempts = retries, BaseDelayMilliseconds = 1 },
                NullLogger.Instance,
                ex => ex is InvalidOperationException));

    [Fact]
    public async Task Read_TransientFailureThenSuccess_ReturnsResult()
    {
        var link = new ShortUrl { Code = "100001" };
        _inner.SetupSequence(r => r.GetByCodeAsync("100001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("blip"))
            .ThrowsAsync(new InvalidOperationException("blip"))
            .ReturnsAsync(link);

        var result = await Create().GetByCodeAsync("100001", CancellationToken.None);

        result.Should().BeSameAs(link);
        _inner.Verify(r => r.GetByCodeAsync("100001", It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Read_AlwaysFails_GivesUpAfterMaxRetries()
    {
        _inner.Setup(r => r.GetByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("down"));

        var act = () => Create(retries: 2).GetByCodeAsync("x", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _inner.Verify(r => r.GetByCodeAsync("x", It.IsAny<CancellationToken>()), Times.Exactly(3)); // 1 + 2 retries
    }

    [Fact]
    public async Task Read_NonTransientFailure_IsNotRetried()
    {
        _inner.Setup(r => r.GetByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("bug"));

        var act = () => Create().GetByCodeAsync("x", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        _inner.Verify(r => r.GetByCodeAsync("x", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_IsNeverRetried()
    {
        _inner.Setup(r => r.AddAsync(It.IsAny<ShortUrl>(), It.IsAny<Func<long, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("blip"));

        var act = () => Create().AddAsync(new ShortUrl(), _ => "c", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _inner.Verify(r => r.AddAsync(It.IsAny<ShortUrl>(), It.IsAny<Func<long, string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveChanges_IsNeverRetried()
    {
        _inner.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("blip"));

        var act = () => Create().SaveChangesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _inner.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Disabled_DoesNotRetry()
    {
        _inner.Setup(r => r.DeleteByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("blip"));

        var act = () => Create(enabled: false).DeleteByCodeAsync("x", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _inner.Verify(r => r.DeleteByCodeAsync("x", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- delegation: each retried method forwards its arguments and retries a transient failure ----

    private static readonly DateTime From = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FindActiveByHash_RetriesAndForwardsArguments()
    {
        var link = new ShortUrl { Code = "100001" };
        _inner.SetupSequence(r => r.FindActiveByHashAsync("h", "https://a", From, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException()).ReturnsAsync(link);

        var result = await Create().FindActiveByHashAsync("h", "https://a", From, CancellationToken.None);

        result.Should().BeSameAs(link);
        _inner.Verify(r => r.FindActiveByHashAsync("h", "https://a", From, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetByCodeForUpdate_RetriesAndForwardsArguments()
    {
        _inner.SetupSequence(r => r.GetByCodeForUpdateAsync("c1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException()).ReturnsAsync(new ShortUrl());

        await Create().GetByCodeForUpdateAsync("c1", CancellationToken.None);

        _inner.Verify(r => r.GetByCodeForUpdateAsync("c1", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetClickStats_RetriesAndForwardsArguments()
    {
        var stats = new ClickStats(3, 2, new Dictionary<string, long>());
        _inner.SetupSequence(r => r.GetClickStatsAsync(42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException()).ReturnsAsync(stats);

        var result = await Create().GetClickStatsAsync(42, CancellationToken.None);

        result.Should().BeSameAs(stats);
    }

    [Fact]
    public async Task GetDailyClicks_RetriesAndForwardsArguments()
    {
        IReadOnlyList<DailyClickRow> rows = new[] { new DailyClickRow(From, 1, 1) };
        _inner.SetupSequence(r => r.GetDailyClicksAsync(7, From, To, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException()).ReturnsAsync(rows);

        var result = await Create().GetDailyClicksAsync(7, From, To, CancellationToken.None);

        result.Should().BeSameAs(rows);
    }

    [Fact]
    public async Task CountryBreakdown_RetriesAndForwardsBounds()
    {
        IReadOnlyList<BreakdownRow> rows = new[] { new BreakdownRow("IN", 2) };
        _inner.SetupSequence(r => r.GetCountryBreakdownAsync(7, From, To, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException()).ReturnsAsync(rows);

        var result = await Create().GetCountryBreakdownAsync(7, From, To, CancellationToken.None);

        result.Should().BeSameAs(rows);
    }

    [Fact]
    public async Task BrowserBreakdown_RetriesAndForwardsBounds()
    {
        IReadOnlyList<BreakdownRow> rows = new[] { new BreakdownRow("Chrome", 2) };
        _inner.SetupSequence(r => r.GetBrowserBreakdownAsync(7, null, To, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException()).ReturnsAsync(rows);

        var result = await Create().GetBrowserBreakdownAsync(7, null, To, CancellationToken.None);

        result.Should().BeSameAs(rows);
    }

    [Fact]
    public async Task DeleteExpired_RetriesAndForwardsArguments()
    {
        _inner.SetupSequence(r => r.DeleteExpiredAsync(From, 500, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException()).ReturnsAsync(12);

        var result = await Create().DeleteExpiredAsync(From, 500, CancellationToken.None);

        result.Should().Be(12);
    }

    [Fact]
    public async Task Add_AndSave_ForwardToInner()
    {
        var entity = new ShortUrl { Code = "c" };
        _inner.Setup(r => r.AddAsync(entity, It.IsAny<Func<long, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);
        var sut = Create();

        (await sut.AddAsync(entity, _ => "c", CancellationToken.None)).Should().BeSameAs(entity);
        await sut.SaveChangesAsync(CancellationToken.None);

        _inner.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- the real production predicate, with real SqlException objects ----

    // SqlException has no public constructor, so build one by reflection carrying the given error numbers.
    private static SqlException SqlExceptionWith(params int[] numbers)
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        var errors = (SqlErrorCollection)RuntimeHelpers.GetUninitializedObject(typeof(SqlErrorCollection));
        var list = new List<object>();
        foreach (var number in numbers)
        {
            var error = RuntimeHelpers.GetUninitializedObject(typeof(SqlError));
            var field = typeof(SqlError).GetFields(All)
                .First(f => f.FieldType == typeof(int) && f.Name.Contains("number", StringComparison.OrdinalIgnoreCase));
            field.SetValue(error, number);
            list.Add(error);
        }

        typeof(SqlErrorCollection).GetFields(All)
            .First(f => f.FieldType == typeof(List<object>))
            .SetValue(errors, list);

        var exception = (SqlException)RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
        typeof(SqlException).GetFields(All)
            .First(f => f.FieldType == typeof(SqlErrorCollection))
            .SetValue(exception, errors);
        return exception;
    }

    [Theory]
    [InlineData(1205)]   // deadlock victim
    [InlineData(-2)]     // timeout
    [InlineData(40613)]  // Azure SQL database unavailable
    [InlineData(10928)]  // resource limit
    public void IsTransientSqlFailure_TransientSqlErrorNumbers_AreRetried(int number)
    {
        ResilientShortUrlRepository.IsTransientSqlFailure(SqlExceptionWith(number)).Should().BeTrue();
    }

    [Theory]
    [InlineData(547)]    // foreign key violation
    [InlineData(2601)]   // duplicate key
    [InlineData(208)]    // invalid object name
    public void IsTransientSqlFailure_PermanentSqlErrorNumbers_AreNotRetried(int number)
    {
        ResilientShortUrlRepository.IsTransientSqlFailure(SqlExceptionWith(number)).Should().BeFalse();
    }

    [Fact]
    public void IsTransientSqlFailure_AnyTransientNumberAmongSeveral_IsRetried()
    {
        ResilientShortUrlRepository.IsTransientSqlFailure(SqlExceptionWith(547, 1205)).Should().BeTrue();
    }

    [Fact]
    public void IsTransientSqlFailure_RecognisesTimeoutsOnly_AmongNonSqlExceptions()
    {
        ResilientShortUrlRepository.IsTransientSqlFailure(new TimeoutException()).Should().BeTrue();
        ResilientShortUrlRepository.IsTransientSqlFailure(new InvalidOperationException()).Should().BeFalse();
    }
}
