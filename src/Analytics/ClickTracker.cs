using UrlShortener.Metrics;
using Microsoft.Extensions.Options;
using UrlShortener.Configurations;

namespace UrlShortener.Analytics;

/// <summary>
/// Called by the redirect endpoint. Builds a ClickRecord and queues it. Does no I/O and never
/// throws: analytics must not be able to break or slow down a redirect.
/// </summary>
public sealed class ClickTracker
{
    private readonly IClickQueue _queue;
    private readonly ICountryResolver _country;
    private readonly TimeProvider _clock;
    private readonly string _salt;
    private readonly ILogger<ClickTracker> _logger;

    public ClickTracker(
        IClickQueue queue,
        ICountryResolver country,
        TimeProvider clock,
        IOptions<AnalyticsOptions> options,
        ILogger<ClickTracker> logger)
    {
        _queue = queue;
        _country = country;
        _clock = clock;
        _salt = options.Value.Salt;
        _logger = logger;
    }

    public void Track(HttpContext context, long shortUrlId)
    {
        try
        {
            var visitor = VisitorHasher.Compute(
                context.Connection.RemoteIpAddress?.ToString(),
                context.Request.Headers.UserAgent.ToString(),
                _salt);

            var userAgent = context.Request.Headers.UserAgent.ToString();

            var click = new ClickRecord(
                shortUrlId,
                _clock.GetUtcNow().UtcDateTime,
                visitor,
                _country.Resolve(context),
                BrowserDetector.Detect(userAgent));

            if (_queue.TryEnqueue(click))
            {
                AppMetrics.ClicksEnqueued.Inc();
            }
            else
            {
                AppMetrics.ClicksDropped.Inc();
                _logger.LogWarning("Click queue is full; dropped a click for link {ShortUrlId}", shortUrlId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record a click for link {ShortUrlId}", shortUrlId);
        }
    }
}
