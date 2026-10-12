using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.DTOs;
using UrlShortener.Exceptions;
using UrlShortener.Repositories;
using static UrlShortener.IntegrationTests.TestHelpers;

namespace UrlShortener.IntegrationTests;

public class ApiEndToEndTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ApiEndToEndTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateNoRedirectClient();
    }

    [Fact]
    public async Task FullLifecycle_Create_Redirect_Analytics_Update_Delete()
    {
        var target = UniqueUrl();

        // Create
        var created = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = target });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await created.Content.ReadFromJsonAsync<ShortUrlResponse>())!;
        var code = CodeOf(body);
        code.Should().MatchRegex("^[0-9A-Za-z]{6,}$");
        body.ExpiresAt.Should().BeAfter(DateTime.UtcNow);

        // Same URL again returns the existing link (200)
        var again = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = target });
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        CodeOf((await again.Content.ReadFromJsonAsync<ShortUrlResponse>())!).Should().Be(code);

        // Redirect is a 302 to the original URL
        var redirect = await VisitAsync(_client, code, ChromeAgent);
        redirect.StatusCode.Should().Be(HttpStatusCode.Found);
        redirect.Headers.Location!.ToString().Should().Be(target);

        // Click shows up in analytics (written by the background worker)
        var analytics = await EventuallyAsync(
            async () => (await _client.GetFromJsonAsync<AnalyticsResponse>($"/api/v1/analytics/{code}"))!,
            a => a.Clicks >= 1);
        analytics.Clicks.Should().Be(1);
        analytics.UniqueVisitors.Should().Be(1);

        // Update changes the target
        var newTarget = UniqueUrl();
        var updated = await _client.PutAsJsonAsync($"/api/v1/shorten/{code}", new { url = newTarget, expiryDays = 10 });
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await VisitAsync(_client, code)).Headers.Location!.ToString().Should().Be(newTarget);

        // Delete, then the link and its analytics are gone
        (await _client.DeleteAsync($"/api/v1/shorten/{code}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await VisitAsync(_client, code)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"/api/v1/analytics/{code}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.DeleteAsync($"/api/v1/shorten/{code}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Redirect_UnknownCode_Returns404ProblemJson()
    {
        var response = await VisitAsync(_client, "zzzzzz9");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain("traceId");
    }

    [Fact]
    public async Task Redirect_ExpiredLink_Returns410()
    {
        var code = await InsertLinkAsync(_factory, DateTime.UtcNow.AddMinutes(-5));

        var response = await VisitAsync(_client, code);

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Update_ExpiredLink_Returns410()
    {
        var code = await InsertLinkAsync(_factory, DateTime.UtcNow.AddMinutes(-5));

        var response = await _client.PutAsJsonAsync($"/api/v1/shorten/{code}", new { url = UniqueUrl() });

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Update_UnknownCode_Returns404()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/shorten/nope123", new { url = UniqueUrl() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    [InlineData(int.MaxValue)]
    public async Task Create_InvalidExpiryDays_Returns400(int days)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = UniqueUrl(), expiryDays = days });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_MissingUrl_Returns400WithFieldError()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/shorten", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Url");
    }

    [Fact]
    public async Task Create_ExpiryDaysSetsExpiry()
    {
        var (_, body) = await CreateAsync(_client, expiryDays: 2);

        body.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(2), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Update_ConcurrentChange_ThrowsConcurrencyConflict()
    {
        // The HTTP API does not expose RowVersion, so the 409 path is proven at the repository level
        // against real SQL Server: two contexts load the same row, the second save must fail.
        var (code, _) = await CreateAsync(_client);

        using var scope1 = _factory.Services.CreateScope();
        using var scope2 = _factory.Services.CreateScope();
        var repo1 = scope1.ServiceProvider.GetRequiredService<IShortUrlRepository>();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IShortUrlRepository>();

        var first = (await repo1.GetByCodeForUpdateAsync(code, CancellationToken.None))!;
        var second = (await repo2.GetByCodeForUpdateAsync(code, CancellationToken.None))!;

        second.OriginalUrl = UniqueUrl();
        await repo2.SaveChangesAsync(CancellationToken.None);

        first.OriginalUrl = UniqueUrl();
        var act = () => repo1.SaveChangesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<ConcurrencyConflictException>();
    }

    [Fact]
    public async Task Health_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    [Fact]
    public async Task Create_ManyDifferentUrls_ReturnsUniqueCodes()
    {
        var tasks = Enumerable.Range(0, 25).Select(_ => CreateAsync(_client));
        var results = await Task.WhenAll(tasks);

        results.Select(r => r.Code).Distinct().Should().HaveCount(25);
    }
}
