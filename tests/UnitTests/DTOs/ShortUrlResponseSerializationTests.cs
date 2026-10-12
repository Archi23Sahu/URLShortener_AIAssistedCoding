using System.Text.Json;
using FluentAssertions;
using UrlShortener.DTOs;

namespace UrlShortener.UnitTests.DTOs;

public class ShortUrlResponseSerializationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ExpiresAt_IsWrittenAsADate_NotATimestamp()
    {
        var response = new ShortUrlResponse("http://short.test/100001", new DateTime(2027, 1, 1, 17, 20, 11, DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(response, Web);

        json.Should().Contain("\"expiresAt\":\"2027-01-01\"");
        json.Should().NotContain("17:20");
    }

    [Theory]
    [InlineData("2027-01-01")]
    [InlineData("2027-01-01T08:00:00Z")]
    public void ExpiresAt_ReadsDateOrTimestamp(string value)
    {
        var json = $"{{\"shortUrl\":\"x\",\"expiresAt\":\"{value}\"}}";

        var response = JsonSerializer.Deserialize<ShortUrlResponse>(json, Web)!;

        response.ExpiresAt.Date.Should().Be(new DateTime(2027, 1, 1));
        response.ExpiresAt.Kind.Should().Be(DateTimeKind.Utc);
    }
}
