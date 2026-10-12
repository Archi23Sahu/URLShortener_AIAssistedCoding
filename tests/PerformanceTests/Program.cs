using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;

// Load test for the redirect endpoint.
//
//   dotnet run --project tests/PerformanceTests -c Release -- [baseUrl] [requests] [concurrency]
//   defaults:                                                  https://localhost:7037  1000  1000
//
// Run the API with rate limiting OFF (otherwise most requests are answered 429, which is correct but not what is measured):
//   PowerShell:  $env:RateLimit__Enabled="false"; dotnet run --project src --urls https://localhost:7037

var baseUrl = args.Length > 0 ? args[0].TrimEnd('/') : "https://localhost:7037";
var total = args.Length > 1 ? int.Parse(args[1]) : 1000;
var concurrency = args.Length > 2 ? int.Parse(args[2]) : 1000;

var handler = new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    MaxConnectionsPerServer = int.MaxValue,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    // Local development certificate only.
    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
};
using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };

Console.WriteLine($"Target: {baseUrl}   requests: {total}   concurrency: {concurrency}");

// 1. Create a link to redirect to.
var create = await client.PostAsJsonAsync("/api/v1/shorten", new { url = $"https://example.com/perf-{Guid.NewGuid():N}" });
create.EnsureSuccessStatusCode();
var created = await create.Content.ReadFromJsonAsync<Created>();
var code = created!.ShortUrl[(created.ShortUrl.LastIndexOf('/') + 1)..];
Console.WriteLine($"Test link: {code}");

// 2. One warm-up request so the first (cache miss) lookup is not mixed into the numbers, and so the connection/JIT cost is paid.
(await client.GetAsync("/" + code)).Dispose();

// 3. Fire all requests, at most `concurrency` at the same time.
var latencies = new double[total];
var statuses = new int[total];
using var gate = new SemaphoreSlim(concurrency);
var clock = Stopwatch.StartNew();

var tasks = Enumerable.Range(0, total).Select(async i =>
{
    await gate.WaitAsync();
    var sw = Stopwatch.StartNew();
    try
    {
        using var response = await client.GetAsync("/" + code, HttpCompletionOption.ResponseHeadersRead);
        statuses[i] = (int)response.StatusCode;
    }
    catch
    {
        statuses[i] = 0; // connection error / timeout
    }
    finally
    {
        latencies[i] = sw.Elapsed.TotalMilliseconds;
        gate.Release();
    }
}).ToArray();

await Task.WhenAll(tasks);
clock.Stop();

// 4. Report.
var sorted = latencies.OrderBy(x => x).ToArray();
double P(double percent) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(percent / 100 * sorted.Length) - 1)];

var ok = statuses.Count(s => s == (int)HttpStatusCode.Found);
var limited = statuses.Count(s => s == 429);
var failed = statuses.Count(s => s == 0);
var other = total - ok - limited - failed;

Console.WriteLine();
Console.WriteLine($"Total time        : {clock.Elapsed.TotalSeconds:F2} s");
Console.WriteLine($"Throughput        : {total / clock.Elapsed.TotalSeconds:F0} requests/s");
Console.WriteLine($"Latency p50       : {P(50):F1} ms");
Console.WriteLine($"Latency p95       : {P(95):F1} ms");
Console.WriteLine($"Latency p99       : {P(99):F1} ms");
Console.WriteLine($"Latency max       : {sorted[^1]:F1} ms");
Console.WriteLine($"302 redirects     : {ok}");
Console.WriteLine($"429 rate limited  : {limited}{(limited > 0 ? "   <-- rate limiting is ON; restart the API with RateLimit__Enabled=false" : "")}");
Console.WriteLine($"Other status      : {other}");
Console.WriteLine($"Connection errors : {failed}");
Console.WriteLine();
Console.WriteLine("Cache hit ratio: read Redis counters before and after the run (see README / chat instructions):");
Console.WriteLine("  docker compose exec redis redis-cli INFO stats   ->  keyspace_hits / keyspace_misses");

return failed == 0 && other == 0 ? 0 : 1;

record Created(string ShortUrl, DateTime ExpiresAt);
