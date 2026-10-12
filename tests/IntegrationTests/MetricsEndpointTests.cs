using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Repositories;
using static UrlShortener.IntegrationTests.TestHelpers;

namespace UrlShortener.IntegrationTests;

public class MetricsEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public MetricsEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateNoRedirectClient();
    }

    [Fact]
    public async Task Metrics_ExposesApplicationAndHttpMetrics_AfterTraffic()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = UniqueUrl() });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await _client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("urlshortener_links_created_total");
        body.Should().Contain("urlshortener_click_queue_depth");
        body.Should().Contain("http_requests_received_total");
    }

    [Fact]
    public async Task Metrics_AfterRedirect_ReportsCacheLookups_AndNeverExposesTheShortCode()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = UniqueUrl() });
        var shortUrl = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("shortUrl").GetString()!;
        var code = shortUrl[(shortUrl.LastIndexOf('/') + 1)..];

        (await _client.GetAsync("/" + code)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await _client.GetAsync("/" + code)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        var body = await (await _client.GetAsync("/metrics")).Content.ReadAsStringAsync();

        body.Should().Contain("urlshortener_cache_lookups_total{result=\"hit\"}");
        body.Should().Contain("urlshortener_cache_lookups_total{result=\"miss\"}");
        // Checked as a label value or path ("=\"<code>" / "/<code>") so a number that happens to contain the digits cannot fail it.
        body.Should().NotContain("=\"" + code).And.NotContain("/" + code, "metrics must not carry per-link data");
    }

    [Fact]
    public void Repository_IsResolvedAsTheRetryDecorator()
    {
        using var scope = _factory.Services.CreateScope();

        var repository = scope.ServiceProvider.GetRequiredService<IShortUrlRepository>();

        repository.Should().BeOfType<ResilientShortUrlRepository>();
    }
}

/// <summary>Resilience switched off: the pipeline is empty but the app must still wire up and serve requests.</summary>
public class ResilienceDisabledFactory : ApiFactory
{
    public ResilienceDisabledFactory() : base(new Dictionary<string, string?> { ["Resilience:Enabled"] = "false" })
    {
    }
}

public class ResilienceDisabledTests : IClassFixture<ResilienceDisabledFactory>
{
    private readonly HttpClient _client;

    public ResilienceDisabledTests(ResilienceDisabledFactory factory)
    {
        _client = factory.CreateNoRedirectClient();
    }

    [Fact]
    public async Task Create_WithResilienceDisabled_StillWorks()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = UniqueUrl() });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}

/// <summary>Uses the tiny-limit host from the rate limit tests (2 reads per minute).</summary>
public class MetricsRateLimitTests : IClassFixture<LimitedApiFactory>
{
    private readonly HttpClient _client;

    public MetricsRateLimitTests(LimitedApiFactory factory)
    {
        _client = factory.CreateNoRedirectClient();
    }

    [Fact]
    public async Task Metrics_IsNeverRateLimited()
    {
        for (var i = 0; i < 6; i++)
        {
            var response = await _client.GetAsync("/metrics");
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"request {i + 1} to /metrics");
        }
    }
}
