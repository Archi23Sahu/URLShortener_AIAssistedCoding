using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static UrlShortener.IntegrationTests.TestHelpers;

namespace UrlShortener.IntegrationTests;

/// <summary>Own host with a tiny limit (3 writes, 2 reads per minute) so 429 is easy to reach.</summary>
public class LimitedApiFactory : ApiFactory
{
    public LimitedApiFactory() : base(new Dictionary<string, string?>
    {
        ["RateLimit:Enabled"] = "true",
        ["RateLimit:Write:PermitLimit"] = "3",
        ["RateLimit:Write:WindowSeconds"] = "60",
        ["RateLimit:Read:PermitLimit"] = "2",
        ["RateLimit:Read:WindowSeconds"] = "60"
    })
    {
    }
}

public class RateLimitIntegrationTests : IClassFixture<LimitedApiFactory>
{
    private readonly HttpClient _client;

    public RateLimitIntegrationTests(LimitedApiFactory factory)
    {
        _client = factory.CreateNoRedirectClient();
    }

    [Fact]
    public async Task Writes_AfterLimit_Return429WithRetryAfter()
    {
        for (var i = 0; i < 3; i++)
        {
            var ok = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = UniqueUrl() });
            ok.StatusCode.Should().Be(HttpStatusCode.Created, $"request {i + 1} is inside the limit");
        }

        var blocked = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = UniqueUrl() });

        blocked.StatusCode.Should().Be((HttpStatusCode)429);
        blocked.Headers.Contains("Retry-After").Should().BeTrue();
        blocked.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("Too many requests");
    }

    [Fact]
    public async Task Reads_AfterLimit_Return429_AndHealthIsNeverLimited()
    {
        (await _client.GetAsync("/zzzzzz9")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/zzzzzz9")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/zzzzzz9")).StatusCode.Should().Be((HttpStatusCode)429);

        for (var i = 0; i < 10; i++)
        {
            (await _client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
