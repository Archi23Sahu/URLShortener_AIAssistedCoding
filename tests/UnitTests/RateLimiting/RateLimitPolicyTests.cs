using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using UrlShortener.Configurations;
using UrlShortener.RateLimiting;

namespace UrlShortener.UnitTests.RateLimiting;

public class RateLimitPolicyTests
{
    private static DefaultHttpContext ContextFrom(string? ip)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = ip is null ? null : IPAddress.Parse(ip);
        return ctx;
    }

    private static RateLimitRule Rule(int permits, int seconds = 60) =>
        new() { PermitLimit = permits, WindowSeconds = seconds };

    [Fact]
    public void Partition_AllowsUpToTheLimit_ThenRejects()
    {
        var partition = RateLimitPolicies.Partition(ContextFrom("203.0.113.9"), enabled: true, Rule(2));
        using var limiter = partition.Factory(partition.PartitionKey);

        limiter.AttemptAcquire().IsAcquired.Should().BeTrue();
        limiter.AttemptAcquire().IsAcquired.Should().BeTrue();
        limiter.AttemptAcquire().IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void Partition_RejectedLease_ReportsRetryAfter()
    {
        var partition = RateLimitPolicies.Partition(ContextFrom("203.0.113.9"), enabled: true, Rule(1, seconds: 30));
        using var limiter = partition.Factory(partition.PartitionKey);
        limiter.AttemptAcquire();

        var rejected = limiter.AttemptAcquire();

        rejected.IsAcquired.Should().BeFalse();
        rejected.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out var retryAfter).Should().BeTrue();
        retryAfter.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Partition_DifferentClients_GetDifferentPartitionKeys()
    {
        var a = RateLimitPolicies.Partition(ContextFrom("203.0.113.1"), enabled: true, Rule(5));
        var b = RateLimitPolicies.Partition(ContextFrom("203.0.113.2"), enabled: true, Rule(5));

        a.PartitionKey.Should().NotBe(b.PartitionKey);
    }

    [Fact]
    public void Partition_SameClient_GetsTheSamePartitionKey()
    {
        var a = RateLimitPolicies.Partition(ContextFrom("203.0.113.1"), enabled: true, Rule(5));
        var b = RateLimitPolicies.Partition(ContextFrom("203.0.113.1"), enabled: true, Rule(5));

        a.PartitionKey.Should().Be(b.PartitionKey);
    }

    [Fact]
    public void Partition_UnknownClientAddress_StillGetsALimiter()
    {
        var partition = RateLimitPolicies.Partition(ContextFrom(null), enabled: true, Rule(1));
        using var limiter = partition.Factory(partition.PartitionKey);

        limiter.AttemptAcquire().IsAcquired.Should().BeTrue();
        limiter.AttemptAcquire().IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void Partition_Disabled_NeverRejects()
    {
        var partition = RateLimitPolicies.Partition(ContextFrom("203.0.113.9"), enabled: false, Rule(1));
        using var limiter = partition.Factory(partition.PartitionKey);

        for (var i = 0; i < 1000; i++)
        {
            limiter.AttemptAcquire().IsAcquired.Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Partition_NonPositiveLimit_IsTreatedAsOne(int permits)
    {
        var partition = RateLimitPolicies.Partition(ContextFrom("203.0.113.9"), enabled: true, Rule(permits));
        using var limiter = partition.Factory(partition.PartitionKey);

        limiter.AttemptAcquire().IsAcquired.Should().BeTrue();
        limiter.AttemptAcquire().IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void Defaults_WritesAreStricterThanReads()
    {
        var options = new RateLimitOptions();

        options.Enabled.Should().BeTrue();
        options.Write.PermitLimit.Should().BeLessThan(options.Read.PermitLimit);
    }
}
