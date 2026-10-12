using UrlShortener.Analytics;
using UrlShortener.DTOs;
using UrlShortener.Exceptions;
using UrlShortener.Repositories;

namespace UrlShortener.Services;

public interface IReportService
{
    /// <summary>Clicks per day. Defaults to the last 30 days (UTC). Days without clicks are included as zero.</summary>
    Task<DailyClicksResponse> GetDailyAsync(string code, DateOnly? from, DateOnly? to, CancellationToken ct);

    Task<CountryBreakdownResponse> GetCountriesAsync(string code, DateOnly? from, DateOnly? to, CancellationToken ct);

    Task<BrowserBreakdownResponse> GetBrowsersAsync(string code, DateOnly? from, DateOnly? to, CancellationToken ct);
}

/// <summary>
/// Reporting queries (Scenario 3). Kept apart from the URL service so the redirect path and its cache
/// decorator are not touched by reporting changes. Dates are UTC calendar days, both ends inclusive.
/// </summary>
public class ReportService : IReportService
{
    public const int DefaultDays = 30;
    public const int MaxRangeDays = 366;

    private readonly IShortUrlRepository _repository;
    private readonly TimeProvider _clock;

    public ReportService(IShortUrlRepository repository, TimeProvider clock)
    {
        _repository = repository;
        _clock = clock;
    }

    public async Task<DailyClicksResponse> GetDailyAsync(string code, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var link = await FindAsync(code, ct);

        var end = to ?? Today();
        var start = from ?? end.AddDays(-(DefaultDays - 1));
        ValidateRange(start, end);

        var rows = await _repository.GetDailyClicksAsync(link.Id, StartOf(start), StartOf(end.AddDays(1)), ct);
        var byDay = rows.ToDictionary(r => DateOnly.FromDateTime(r.Date));

        var days = new List<DailyClickPoint>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            days.Add(byDay.TryGetValue(day, out var row)
                ? new DailyClickPoint(day, row.Clicks, row.UniqueVisitors)
                : new DailyClickPoint(day, 0, 0));
        }

        return new DailyClicksResponse(code, start, end, days.Sum(d => d.Clicks), days);
    }

    public async Task<CountryBreakdownResponse> GetCountriesAsync(string code, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var link = await FindAsync(code, ct);
        var (fromUtc, toExclusiveUtc) = OptionalRange(from, to);

        var rows = await _repository.GetCountryBreakdownAsync(link.Id, fromUtc, toExclusiveUtc, ct);

        var total = rows.Sum(r => r.Clicks);
        var unknown = rows.Where(r => r.Key is null).Sum(r => r.Clicks);
        var countries = rows
            .Where(r => r.Key is not null)
            .OrderByDescending(r => r.Clicks)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new CountryStat(r.Key!, r.Clicks, Percent(r.Clicks, total)))
            .ToList();

        return new CountryBreakdownResponse(code, total, unknown, countries);
    }

    public async Task<BrowserBreakdownResponse> GetBrowsersAsync(string code, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var link = await FindAsync(code, ct);
        var (fromUtc, toExclusiveUtc) = OptionalRange(from, to);

        var rows = await _repository.GetBrowserBreakdownAsync(link.Id, fromUtc, toExclusiveUtc, ct);

        // Clicks recorded before the Browser column existed have no value; report them as "Unknown".
        var merged = rows
            .GroupBy(r => r.Key ?? BrowserDetector.Unknown)
            .Select(g => (Browser: g.Key, Clicks: g.Sum(r => r.Clicks)))
            .ToList();

        var total = merged.Sum(m => m.Clicks);
        var browsers = merged
            .OrderByDescending(m => m.Clicks)
            .ThenBy(m => m.Browser, StringComparer.Ordinal)
            .Select(m => new BrowserStat(m.Browser, m.Clicks, Percent(m.Clicks, total)))
            .ToList();

        return new BrowserBreakdownResponse(code, total, browsers);
    }

    private async Task<Entities.ShortUrl> FindAsync(string code, CancellationToken ct) =>
        await _repository.GetByCodeAsync(code, ct) ?? throw new NotFoundException(code);

    private (DateTime? FromUtc, DateTime? ToExclusiveUtc) OptionalRange(DateOnly? from, DateOnly? to)
    {
        if (from.HasValue && to.HasValue)
        {
            ValidateRange(from.Value, to.Value);
        }

        return (from.HasValue ? StartOf(from.Value) : null,
                to.HasValue ? StartOf(to.Value.AddDays(1)) : null);
    }

    private static void ValidateRange(DateOnly start, DateOnly end)
    {
        if (start > end)
        {
            throw new ArgumentException("'from' must be on or before 'to'.");
        }

        if (end.DayNumber - start.DayNumber + 1 > MaxRangeDays)
        {
            throw new ArgumentException($"The date range may not be longer than {MaxRangeDays} days.");
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    private static DateTime StartOf(DateOnly day) => day.ToDateTime(TimeOnly.MinValue);

    private static double Percent(long part, long total) =>
        total == 0 ? 0 : Math.Round(part * 100.0 / total, 1);
}
