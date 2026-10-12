using FluentValidation;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using UrlShortener.Analytics;
using UrlShortener.BackgroundServices;
using UrlShortener.Caching;
using UrlShortener.Configurations;
using UrlShortener.Middleware;
using UrlShortener.RateLimiting;
using UrlShortener.Repositories;
using UrlShortener.Services;
using UrlShortener.Validators;
using Serilog;
using Microsoft.Extensions.Options;
using Polly;
using Prometheus;
using UrlShortener.Metrics;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.Configure<ShortenerOptions>(builder.Configuration.GetSection(ShortenerOptions.SectionName));

builder.Services.Configure<AnalyticsOptions>(builder.Configuration.GetSection(AnalyticsOptions.SectionName));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<ResilienceOptions>(builder.Configuration.GetSection(ResilienceOptions.SectionName));
builder.Services.AddSingleton(sp => ResilientShortUrlRepository.CreatePipeline(
    sp.GetRequiredService<IOptions<ResilienceOptions>>().Value,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<ResilientShortUrlRepository>()));
// Polly retries (transient SQL errors) wrap the repository; only idempotent operations are retried.
builder.Services.AddScoped<ShortUrlRepository>();
builder.Services.AddScoped<IShortUrlRepository>(sp => new ResilientShortUrlRepository(
    sp.GetRequiredService<ShortUrlRepository>(),
    sp.GetRequiredService<ResiliencePipeline>()));

// Task 5: Redis cache-aside on the redirect path. CachedUrlShortenerService wraps the real service.
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection(CacheOptions.SectionName));
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        var redis = ConfigurationOptions.Parse(redisConnection);
        redis.AbortOnConnectFail = false; // start even if Redis is down; reconnect in the background
        redis.ConnectTimeout = 1000;      // short timeouts: a dead cache must not make redirects slow
        redis.SyncTimeout = 500;
        redis.AsyncTimeout = 500;
        options.ConfigurationOptions = redis;
        options.InstanceName = "urlshortener:";
    });
}
else
{
    // No Redis configured (for example in tests): same behaviour, in-process memory.
    builder.Services.AddDistributedMemoryCache();
}
builder.Services.AddSingleton<CacheHealth>();
builder.Services.AddScoped<UrlShortenerService>();
builder.Services.AddScoped<IUrlShortenerService>(sp =>
    ActivatorUtilities.CreateInstance<CachedUrlShortenerService>(sp, sp.GetRequiredService<UrlShortenerService>()));
builder.Services.AddValidatorsFromAssemblyContaining<CreateShortUrlRequestValidator>();

// Task 6: asynchronous click analytics (queue -> background worker -> ClickEvents).
builder.Services.AddSingleton<IClickQueue, ChannelClickQueue>();
builder.Services.AddSingleton<ICountryResolver, HeaderCountryResolver>();
builder.Services.AddSingleton<ClickTracker>();
builder.Services.AddHostedService<ClickRecorderBackgroundService>();

// Scenario 2: rate limiting (per client IP) and background cleanup of long-expired links.
builder.Services.AddAppRateLimiting(builder.Configuration);

builder.Services.Configure<CleanupOptions>(builder.Configuration.GetSection(CleanupOptions.SectionName));
builder.Services.AddScoped<ExpiredLinkCleaner>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddHostedService<ExpiredLinkCleanupService>();

// FluentValidation reports missing/invalid fields, so turn off MVC's implicit [Required]
// for non-nullable reference types. That keeps every 400 in one consistent format.
builder.Services.AddControllers(options =>
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseSwagger();
app.UseSwaggerUI();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await DatabaseInitializer.MigrateWithRetryAsync(app.Services, app.Logger);
}

// After routing (added automatically) so [EnableRateLimiting] on controllers is visible; before the endpoints run.
app.UseRateLimiter();

// Metrics: HTTP request counters/latency plus the application metrics in AppMetrics, at /metrics.
app.UseHttpMetrics();
var clickQueue = app.Services.GetRequiredService<UrlShortener.Analytics.IClickQueue>();
Metrics.DefaultRegistry.AddBeforeCollectCallback(() => AppMetrics.ClickQueueDepth.Set(clickQueue.Reader.Count));

app.MapControllers();
app.MapHealthChecks("/health");
app.MapMetrics();

app.Run();

// Makes Program visible to WebApplicationFactory<Program> in the integration tests (Day 2).
public partial class Program { }
