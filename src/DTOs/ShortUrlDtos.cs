using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UrlShortener.DTOs;

/// <param name="Url">Absolute http or https URL to shorten.</param>
/// <param name="ExpiryDays">Optional lifetime in days. Defaults to Shortener:DefaultExpiryDays.</param>
public record CreateShortUrlRequest(string Url, int? ExpiryDays = null);

/// <param name="Url">New absolute http or https target.</param>
/// <param name="ExpiryDays">Optional. When set, expiry is reset to now plus this many days. When omitted, expiry is unchanged.</param>
public record UpdateShortUrlRequest(string Url, int? ExpiryDays = null);

/// <param name="ShortUrl">The short link.</param>
/// <param name="ExpiresAt">
/// Serialized as a UTC calendar date ("2027-01-01"), as in the assessment contract. The service still keeps
/// and enforces the exact expiry instant; the link stops working at that time on the date shown.
/// </param>
public record ShortUrlResponse(
    string ShortUrl,
    [property: JsonConverter(typeof(UtcDateOnlyConverter))] DateTime ExpiresAt);

public record AnalyticsResponse(long Clicks, long UniqueVisitors, IReadOnlyDictionary<string, long> Countries);

/// <summary>Writes a DateTime as yyyy-MM-dd (UTC). Reads either a date or a full timestamp.</summary>
public sealed class UtcDateOnlyConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? throw new JsonException("expiresAt must be a date string.");

        return DateTime.Parse(
            text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
