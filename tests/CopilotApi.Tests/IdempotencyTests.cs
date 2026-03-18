using System.Text.Json;
using CopilotPluginApi.Models;
using CopilotPluginApi.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace CopilotApi.Tests;

/// <summary>
/// Verifies idempotent response caching and retrieval via SHA-256 hashed Redis keys.
/// </summary>
public sealed class IdempotencyTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly Mock<IDatabase> _mockDatabase = new();
    private readonly Mock<ILogger<IdempotencyService>> _mockLogger = new();

    private IdempotencyService CreateService()
    {
        var mockMultiplexer = new Mock<IConnectionMultiplexer>();
        mockMultiplexer
            .Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockDatabase.Object);

        return new IdempotencyService(mockMultiplexer.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task GetAsync_ExistingRequestId_ReturnsCachedResponse()
    {
        var expected = new ChatResponse
        {
            Response = "cached reply",
            ModelUsed = "gpt-4o",
            CacheHit = false,
            Degraded = false,
            PromptTokens = 10,
            CompletionTokens = 5,
            LatencyMs = 42.5
        };
        var serialized = JsonSerializer.Serialize(expected, SerializerOptions);
        _mockDatabase
            .Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)serialized);
        var service = CreateService();

        var result = await service.GetAsync("known-request-id");

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetAsync_UnknownRequestId_ReturnsNull()
    {
        _mockDatabase
            .Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        var service = CreateService();

        var result = await service.GetAsync("unknown-request-id");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_RedisUnavailable_ReturnsNull()
    {
        _mockDatabase
            .Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable"));
        var service = CreateService();

        var result = await service.GetAsync("any-request-id");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_RedisUnavailable_DoesNotThrow()
    {
        _mockDatabase
            .Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable"));
        var service = CreateService();
        var response = new ChatResponse { Response = "test", ModelUsed = "gpt-4o" };

        var act = () => service.SetAsync("fail-request-id", response);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetAsync_KeyUsesHashedRequestId()
    {
        RedisKey capturedKey = default;
        _mockDatabase
            .Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, CommandFlags>((key, _) => capturedKey = key)
            .ReturnsAsync(RedisValue.Null);
        var service = CreateService();

        await service.GetAsync("test-request-id");

        capturedKey.ToString().Should().StartWith("idem:")
            .And.NotContain("test-request-id");
    }
}
