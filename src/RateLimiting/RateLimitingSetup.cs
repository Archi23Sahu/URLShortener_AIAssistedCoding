using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using UrlShortener.Configurations;

namespace UrlShortener.RateLimiting;

public static class RateLimitPolicies
{
    public const string Write = "write";
    public const string Read = "read";

    /// <summary>
    /// One fixed-window limiter per client IP. A fixed window is the simplest option; its known weakness is
    /// that a client can send up to twice the limit across a window boundary.
    /// </summary>
    public static RateLimitPartition<string> Partition(HttpContext context, bool enabled, RateLimitRule rule)
    {
        if (!enabled)
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }

        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(client, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, rule.PermitLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, rule.WindowSeconds)),
            QueueLimit = 0, // reject immediately; never make a client wait
            AutoReplenishment = true
        });
    }
}

public static class RateLimitingSetup
{
    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Options are read per request (not at startup) so configuration overrides always apply.
            limiter.AddPolicy(RateLimitPolicies.Write, context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                return RateLimitPolicies.Partition(context, options.Enabled, options.Write);
            });

            limiter.AddPolicy(RateLimitPolicies.Read, context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                return RateLimitPolicies.Partition(context, options.Enabled, options.Read);
            });

            limiter.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter =
                        Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("RateLimiting")
                    .LogWarning("Rate limit exceeded for {Client} on {Method} {Path}",
                        http.Connection.RemoteIpAddress, http.Request.Method, http.Request.Path);

                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests",
                    Detail = "Rate limit exceeded. Wait for the number of seconds in the Retry-After header and try again."
                };
                problem.Extensions["traceId"] = http.TraceIdentifier;

                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                http.Response.ContentType = "application/problem+json";
                await http.Response.WriteAsync(JsonSerializer.Serialize(problem), ct);
            };
        });

        return services;
    }
}
