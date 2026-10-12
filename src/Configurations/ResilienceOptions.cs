namespace UrlShortener.Configurations;

public class ResilienceOptions
{
    public const string SectionName = "Resilience";

    /// <summary>Retry transient database failures on idempotent operations.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Retries after the first attempt (so 3 means up to 4 tries).</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Delay before the first retry; doubles each time, with jitter.</summary>
    public int BaseDelayMilliseconds { get; set; } = 100;
}
