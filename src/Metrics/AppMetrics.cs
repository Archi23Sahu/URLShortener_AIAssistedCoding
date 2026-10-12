using Prometheus;

namespace UrlShortener.Metrics;

/// <summary>
/// Application metrics exposed at /metrics in Prometheus format (HTTP request metrics come from
/// prometheus-net's middleware). Static on purpose: counters are process-wide and cheap to increment,
/// so the hot paths take no extra dependency and tests need no setup.
/// </summary>
public static class AppMetrics
{
    public static readonly Counter LinksCreated = Prometheus.Metrics.CreateCounter(
        "urlshortener_links_created_total", "Short URLs created (duplicates are not counted).");

    public static readonly Counter CacheLookups = Prometheus.Metrics.CreateCounter(
        "urlshortener_cache_lookups_total", "Redirect cache lookups by result.",
        new CounterConfiguration { LabelNames = new[] { "result" } });

    public static readonly Counter ClicksEnqueued = Prometheus.Metrics.CreateCounter(
        "urlshortener_clicks_enqueued_total", "Clicks placed on the in-memory queue.");

    public static readonly Counter ClicksDropped = Prometheus.Metrics.CreateCounter(
        "urlshortener_clicks_dropped_total", "Clicks dropped because the queue was full.");

    public static readonly Counter ClicksStored = Prometheus.Metrics.CreateCounter(
        "urlshortener_clicks_stored_total", "Clicks written to the database.");

    public static readonly Gauge ClickQueueDepth = Prometheus.Metrics.CreateGauge(
        "urlshortener_click_queue_depth", "Clicks currently waiting in the in-memory queue.");

    public static readonly Counter DatabaseRetries = Prometheus.Metrics.CreateCounter(
        "urlshortener_database_retries_total", "Retries of transient database failures (Polly).");

    public static readonly Counter ExpiredLinksDeleted = Prometheus.Metrics.CreateCounter(
        "urlshortener_expired_links_deleted_total", "Expired links removed by the cleanup job.");
}
