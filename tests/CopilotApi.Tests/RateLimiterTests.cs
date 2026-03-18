using CopilotPluginApi.Configuration;
using CopilotPluginApi.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace CopilotApi.Tests;

/// <summary>
/// Verifies per-user rate limiting via the Redis token bucket implementation.
/// </summary>
public sealed class RateLimiterTests
{
    private const int TestRequestsPerMinute = 100;

    private readonly Mock<IDatabase> _mockDatabase = new();
    private readonly Mock<ILogger<RateLimiterService>> _mockLogger = new();

    private RateLimiterService CreateService()
    {
        var mockMultiplexer = new Mock<IConnectionMultiplexer>();
        mockMultiplexer
            .Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockDatabase.Object);

        var options = Options.Create(new RateLimitConfig { RequestsPerMinute = TestRequestsPerMinute });

        return new RateLimiterService(
            mockMultiplexer.Object,
            options,
            TimeProvider.System,
            _mockLogger.Object);
    }

    [Fact]
    public async Task IsAllowedAsync_WithinLimit_ReturnsTrue()
    {
        _mockDatabase
            .Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]?>(),
                It.IsAny<RedisValue[]?>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)1));
        var service = CreateService();

        var result = await service.IsAllowedAsync("user-1");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_LimitExceeded_ReturnsFalse()
    {
        _mockDatabase
            .Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]?>(),
                It.IsAny<RedisValue[]?>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)0));
        var service = CreateService();

        var result = await service.IsAllowedAsync("user-1");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_RedisUnavailable_FailsOpenReturnsTrue()
    {
        _mockDatabase
            .Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]?>(),
                It.IsAny<RedisValue[]?>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable"));
        var service = CreateService();

        var result = await service.IsAllowedAsync("user-1");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_UsesCorrectRedisKeyPattern()
    {
        RedisKey[]? capturedKeys = null;
        _mockDatabase
            .Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]?>(),
                It.IsAny<RedisValue[]?>(),
                It.IsAny<CommandFlags>()))
            .Callback<string, RedisKey[]?, RedisValue[]?, CommandFlags>((_, keys, _, _) => capturedKeys = keys)
            .ReturnsAsync(RedisResult.Create((RedisValue)1));
        var service = CreateService();

        await service.IsAllowedAsync("user-123");

        capturedKeys.Should().ContainSingle()
            .Which.ToString().Should().Be("ratelimit:{user-123}");
    }
}
