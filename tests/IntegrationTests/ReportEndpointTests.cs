using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using UrlShortener.DTOs;
using static UrlShortener.IntegrationTests.TestHelpers;

namespace UrlShortener.IntegrationTests;

public class ReportEndpointTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public ReportEndpointTests(ApiFactory factory)
    {
        _client = factory.CreateNoRedirectClient();
    }

    private async Task<string> LinkWithClicksAsync()
    {
        var (code, _) = await CreateAsync(_client);
        (await VisitAsync(_client, code, ChromeAgent, "IN")).StatusCode.Should().Be(HttpStatusCode.Found);
        (await VisitAsync(_client, code, ChromeAgent, "IN")).StatusCode.Should().Be(HttpStatusCode.Found);
        (await VisitAsync(_client, code, "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0", "US"))
            .StatusCode.Should().Be(HttpStatusCode.Found);
        (await VisitAsync(_client, code, "curl/8.4.0")).StatusCode.Should().Be(HttpStatusCode.Found);
        return code;
    }

    [Fact]
    public async Task Countries_ReportsKnownAndUnknown()
    {
        var code = await LinkWithClicksAsync();

        var report = await EventuallyAsync(
            async () => (await _client.GetFromJsonAsync<CountryBreakdownResponse>($"/api/v1/analytics/{code}/countries"))!,
            r => r.TotalClicks >= 4);

        report.TotalClicks.Should().Be(4);
        report.UnknownClicks.Should().Be(1);
        report.Countries.Select(c => (c.Country, c.Clicks)).Should().Equal(("IN", 2L), ("US", 1L));
        report.Countries[0].Percentage.Should().Be(50.0);
    }

    [Fact]
    public async Task Browsers_ReportsDetectedFamilies()
    {
        var code = await LinkWithClicksAsync();

        var report = await EventuallyAsync(
            async () => (await _client.GetFromJsonAsync<BrowserBreakdownResponse>($"/api/v1/analytics/{code}/browsers"))!,
            r => r.TotalClicks >= 4);

        report.Browsers.Select(b => (b.Browser, b.Clicks)).Should().Equal(("Chrome", 2L), ("Bot", 1L), ("Firefox", 1L));
    }

    [Fact]
    public async Task Daily_DefaultRange_IsThirtyDaysEndingTodayWithTodaysClicks()
    {
        var code = await LinkWithClicksAsync();

        var report = await EventuallyAsync(
            async () => (await _client.GetFromJsonAsync<DailyClicksResponse>($"/api/v1/analytics/{code}/daily"))!,
            r => r.TotalClicks >= 4);

        report.Days.Should().HaveCount(30);
        report.To.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
        report.TotalClicks.Should().Be(4);
        report.Days[^1].Clicks.Should().Be(4);
        report.Days[^1].UniqueVisitors.Should().BeGreaterThan(0);
        report.Days.Take(29).Should().OnlyContain(d => d.Clicks == 0);
    }

    [Fact]
    public async Task Daily_ExplicitRange_ReturnsOneEntryPerDay()
    {
        var (code, _) = await CreateAsync(_client);

        var report = await _client.GetFromJsonAsync<DailyClicksResponse>(
            $"/api/v1/analytics/{code}/daily?from=2026-01-01&to=2026-01-10");

        report!.Days.Should().HaveCount(10);
        report.TotalClicks.Should().Be(0);
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("countries")]
    [InlineData("browsers")]
    public async Task Reports_FromAfterTo_Return400(string report)
    {
        var (code, _) = await CreateAsync(_client);

        var response = await _client.GetAsync($"/api/v1/analytics/{code}/{report}?from=2026-02-10&to=2026-02-01");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("countries")]
    [InlineData("browsers")]
    public async Task Reports_NotADate_Return400(string report)
    {
        var (code, _) = await CreateAsync(_client);

        var response = await _client.GetAsync($"/api/v1/analytics/{code}/{report}?from=30&to=366");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Daily_RangeOverOneYear_Returns400()
    {
        var (code, _) = await CreateAsync(_client);

        var response = await _client.GetAsync($"/api/v1/analytics/{code}/daily?from=2024-01-01&to=2026-01-01");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("countries")]
    [InlineData("browsers")]
    public async Task Reports_UnknownCode_Return404(string report)
    {
        var response = await _client.GetAsync($"/api/v1/analytics/zzzzzz9/{report}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
