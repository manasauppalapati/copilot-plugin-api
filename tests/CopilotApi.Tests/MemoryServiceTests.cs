using System.Text.Json;
using CopilotPluginApi.Configuration;
using CopilotPluginApi.Models;
using CopilotPluginApi.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace CopilotApi.Tests;

/// <summary>
/// Verifies bounded conversation memory with Redis-backed storage and token-budget trimming.
/// </summary>
public sealed class MemoryServiceTests
{
    private const int TestMaxTurns = 20;
    private const int TestTtlHours = 24;
    private const int TestTokenBudget = 3000;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly Mock<IDatabase> _mockDatabase = new();
    private readonly Mock<ILogger<MemoryService>> _mockLogger = new();

    private MemoryService CreateService(Tokenizer? tokenizer = null)
    {
        var mockMultiplexer = new Mock<IConnectionMultiplexer>();
        mockMultiplexer
            .Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockDatabase.Object);

        var options = Options.Create(new MemoryConfig
        {
            MaxTurns = TestMaxTurns,
            TtlHours = TestTtlHours,
            TokenBudget = TestTokenBudget
        });

        tokenizer ??= TiktokenTokenizer.CreateForModel("gpt-4o");

        return new MemoryService(
            mockMultiplexer.Object,
            options,
            tokenizer,
            _mockLogger.Object);
    }

    [Fact]
    public async Task GetHistoryAsync_ExistingSession_ReturnsOrderedTurns()
    {
        var turns = new[]
        {
            new ConversationTurn { Role = "user", Content = "First message", Timestamp = DateTime.UtcNow.AddMinutes(-2) },
            new ConversationTurn { Role = "assistant", Content = "First reply", Timestamp = DateTime.UtcNow.AddMinutes(-1) },
            new ConversationTurn { Role = "user", Content = "Second message", Timestamp = DateTime.UtcNow }
        };
        var serialized = turns.Select(t => (RedisValue)JsonSerializer.Serialize(t, SerializerOptions)).ToArray();
        _mockDatabase
            .Setup(db => db.ListRangeAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(serialized);
        var service = CreateService();

        var result = await service.GetHistoryAsync("session-1");

        result.Should().HaveCount(3)
            .And.SatisfyRespectively(
                first => first.Content.Should().Be("First message"),
                second => second.Content.Should().Be("First reply"),
                third => third.Content.Should().Be("Second message"));
    }

    [Fact]
    public async Task GetHistoryAsync_RedisUnavailable_ReturnsEmptyList()
    {
        _mockDatabase
            .Setup(db => db.ListRangeAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable"));
        var service = CreateService();

        var result = await service.GetHistoryAsync("session-1");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendTurnAsync_ExceedsMaxTurns_TrimsOldestTurn()
    {
        var mockTransaction = new Mock<ITransaction>();
        mockTransaction
            .Setup(t => t.ListRightPushAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult((long)(TestMaxTurns + 1)));
        mockTransaction
            .Setup(t => t.ListTrimAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .Returns(Task.CompletedTask);
        mockTransaction
            .Setup(t => t.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));
        mockTransaction
            .Setup(t => t.ExecuteAsync(It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));
        _mockDatabase
            .Setup(db => db.CreateTransaction(It.IsAny<object?>()))
            .Returns(mockTransaction.Object);
        var service = CreateService();
        var turn = new ConversationTurn { Role = "user", Content = "overflow", Timestamp = DateTime.UtcNow };

        await service.AppendTurnAsync("session-1", turn);

        mockTransaction.Verify(
            t => t.ListTrimAsync(
                It.IsAny<RedisKey>(),
                It.Is<long>(start => start == -TestMaxTurns),
                It.Is<long>(stop => stop == -1),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task AppendTurnAsync_AlwaysResetsTTL()
    {
        var mockTransaction = new Mock<ITransaction>();
        mockTransaction
            .Setup(t => t.ListRightPushAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(1L));
        mockTransaction
            .Setup(t => t.ListTrimAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .Returns(Task.CompletedTask);
        mockTransaction
            .Setup(t => t.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));
        mockTransaction
            .Setup(t => t.ExecuteAsync(It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));
        _mockDatabase
            .Setup(db => db.CreateTransaction(It.IsAny<object?>()))
            .Returns(mockTransaction.Object);
        var service = CreateService();
        var turn = new ConversationTurn { Role = "user", Content = "hello", Timestamp = DateTime.UtcNow };

        await service.AppendTurnAsync("session-1", turn);

        mockTransaction.Verify(
            t => t.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                It.Is<TimeSpan?>(ttl => ttl.HasValue && ttl.Value == TimeSpan.FromHours(TestTtlHours)),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public void TrimToTokenBudget_HistoryExceedsBudget_DropsOldestTurns()
    {
        var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
        var service = CreateService(tokenizer);
        var sampleTurn = new ConversationTurn { Role = "user", Content = "Hello, how are you doing today?", Timestamp = DateTime.UtcNow };
        var tokensPerTurn = tokenizer.CountTokens($"{sampleTurn.Role}\n{sampleTurn.Content}");
        var history = Enumerable.Range(0, 5)
            .Select(i => new ConversationTurn { Role = "user", Content = "Hello, how are you doing today?", Timestamp = DateTime.UtcNow.AddMinutes(i) })
            .ToList();
        var budget = tokensPerTurn * 3;

        var result = service.TrimToTokenBudget(history, budget);

        result.Should().HaveCount(3)
            .And.BeEquivalentTo(history.Skip(2), options => options.WithStrictOrdering());
    }

    [Fact]
    public void TrimToTokenBudget_HistoryWithinBudget_ReturnsAllTurns()
    {
        var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
        var service = CreateService(tokenizer);
        var history = new List<ConversationTurn>
        {
            new() { Role = "user", Content = "Hi", Timestamp = DateTime.UtcNow.AddMinutes(-2) },
            new() { Role = "assistant", Content = "Hello!", Timestamp = DateTime.UtcNow.AddMinutes(-1) },
            new() { Role = "user", Content = "How are you?", Timestamp = DateTime.UtcNow }
        };

        var result = service.TrimToTokenBudget(history, TestTokenBudget);

        result.Should().HaveCount(3)
            .And.BeEquivalentTo(history, options => options.WithStrictOrdering());
    }
}
