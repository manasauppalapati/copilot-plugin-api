using System.Text.Json;
using CopilotPluginApi.Configuration;
using CopilotPluginApi.Models;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using StackExchange.Redis;

namespace CopilotPluginApi.Services;

/// <summary>
/// Defines the contract for reading and writing bounded conversation history.
/// </summary>
public interface IMemoryService
{
    /// <summary>
    /// Retrieves the stored conversation history for the supplied session identifier.
    /// </summary>
    /// <param name="sessionId">The session identifier that scopes the conversation history.</param>
    /// <param name="ct">The cancellation token for the operation.</param>
    /// <returns>The stored conversation turns ordered from oldest to newest.</returns>
    Task<IReadOnlyList<ConversationTurn>> GetHistoryAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Appends a conversation turn to the stored session history and refreshes the session expiration.
    /// </summary>
    /// <param name="sessionId">The session identifier that scopes the conversation history.</param>
    /// <param name="turn">The conversation turn to append.</param>
    /// <param name="ct">The cancellation token for the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task AppendTurnAsync(string sessionId, ConversationTurn turn, CancellationToken ct = default);

    /// <summary>
    /// Clears the stored conversation history for the supplied session identifier.
    /// </summary>
    /// <param name="sessionId">The session identifier that scopes the conversation history.</param>
    /// <param name="ct">The cancellation token for the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ClearAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Removes the oldest conversation turns until the remaining history fits within the supplied token budget.
    /// </summary>
    /// <param name="history">The conversation history ordered from oldest to newest.</param>
    /// <param name="tokenBudget">The maximum number of tokens permitted for the returned history.</param>
    /// <returns>The newest suffix of <paramref name="history" /> that fits within <paramref name="tokenBudget" />.</returns>
    IReadOnlyList<ConversationTurn> TrimToTokenBudget(IReadOnlyList<ConversationTurn> history, int tokenBudget);
}

/// <summary>
/// Stores bounded conversation history in Redis and trims it to a token budget when requested.
/// </summary>
/// <param name="connectionMultiplexer">The Redis connection multiplexer.</param>
/// <param name="memoryOptions">The configured memory bounds and session expiration values.</param>
/// <param name="tokenizer">The tokenizer used for deterministic history token counting.</param>
/// <param name="logger">The logger used for non-fatal Redis and serialization failures.</param>
public sealed class MemoryService(
    IConnectionMultiplexer connectionMultiplexer,
    IOptions<MemoryConfig> memoryOptions,
    Tokenizer tokenizer,
    ILogger<MemoryService> logger) : IMemoryService
{
    private const string RedisKeyPrefix = "session:";
    private const string RedisKeySuffix = ":history";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDatabase database = connectionMultiplexer.GetDatabase();
    private readonly int maxTurns = memoryOptions.Value.MaxTurns;
    private readonly TimeSpan timeToLive = TimeSpan.FromHours(memoryOptions.Value.TtlHours);
    private readonly Tokenizer historyTokenizer = tokenizer;
    private readonly ILogger<MemoryService> serviceLogger = logger;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationTurn>> GetHistoryAsync(string sessionId, CancellationToken ct = default)
    {
        ValidateSessionId(sessionId);
        ct.ThrowIfCancellationRequested();

        var key = BuildRedisKey(sessionId);

        try
        {
            var storedTurns = await database.ListRangeAsync(key).ConfigureAwait(false);
            if (storedTurns.Length == 0)
            {
                return Array.Empty<ConversationTurn>();
            }

            var history = new List<ConversationTurn>(storedTurns.Length);

            foreach (var storedTurn in storedTurns)
            {
                ct.ThrowIfCancellationRequested();

                if (storedTurn.IsNullOrEmpty)
                {
                    continue;
                }

                try
                {
                    var turn = JsonSerializer.Deserialize<ConversationTurn>(storedTurn.ToString(), SerializerOptions);
                    if (turn is not null)
                    {
                        history.Add(turn);
                    }
                }
                catch (JsonException ex)
                {
                    serviceLogger.LogWarning(
                        ex,
                        "Failed to deserialize a stored conversation turn for session {SessionId}. The malformed entry was skipped.",
                        sessionId);
                }
            }

            return history;
        }
        catch (RedisException ex)
        {
            serviceLogger.LogWarning(
                ex,
                "Redis memory retrieval failed for session {SessionId}. Returning empty history.",
                sessionId);

            return Array.Empty<ConversationTurn>();
        }
    }

    /// <inheritdoc />
    public async Task AppendTurnAsync(string sessionId, ConversationTurn turn, CancellationToken ct = default)
    {
        ValidateSessionId(sessionId);
        ArgumentNullException.ThrowIfNull(turn);
        ct.ThrowIfCancellationRequested();

        var key = BuildRedisKey(sessionId);

        try
        {
            var payload = JsonSerializer.Serialize(turn, SerializerOptions);
            var transaction = database.CreateTransaction();

            var appendTask = transaction.ListRightPushAsync(key, payload);
            var trimTask = transaction.ListTrimAsync(key, -maxTurns, -1);
            var expireTask = transaction.KeyExpireAsync(key, timeToLive);

            var committed = await transaction.ExecuteAsync().ConfigureAwait(false);
            if (!committed)
            {
                serviceLogger.LogWarning(
                    "Redis memory transaction did not commit for session {SessionId}. Conversation history was not updated.",
                    sessionId);

                return;
            }

            await Task.WhenAll(appendTask, trimTask, expireTask).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            serviceLogger.LogWarning(
                ex,
                "Redis memory write failed for session {SessionId}. Continuing without persisted conversation history.",
                sessionId);
        }
        catch (NotSupportedException ex)
        {
            serviceLogger.LogWarning(
                ex,
                "Conversation turn serialization failed for session {SessionId}. Continuing without persisted conversation history.",
                sessionId);
        }
    }

    /// <inheritdoc />
    public async Task ClearAsync(string sessionId, CancellationToken ct = default)
    {
        ValidateSessionId(sessionId);
        ct.ThrowIfCancellationRequested();

        var key = BuildRedisKey(sessionId);

        try
        {
            await database.KeyDeleteAsync(key).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            serviceLogger.LogWarning(
                ex,
                "Redis memory clear failed for session {SessionId}. Continuing without clearing the stored conversation history.",
                sessionId);
        }
    }

    /// <summary>
    /// Removes the oldest conversation turns until the remaining history fits within the supplied token budget.
    /// </summary>
    /// <param name="history">The conversation history ordered from oldest to newest.</param>
    /// <param name="tokenBudget">The maximum number of tokens permitted for the returned history.</param>
    /// <returns>The newest suffix of <paramref name="history" /> that fits within <paramref name="tokenBudget" />.</returns>
    public IReadOnlyList<ConversationTurn> TrimToTokenBudget(IReadOnlyList<ConversationTurn> history, int tokenBudget)
        => PromptTokenCalculator.TrimToTokenBudget(historyTokenizer, history, tokenBudget);

    private static RedisKey BuildRedisKey(string sessionId) =>
        $"{RedisKeyPrefix}{{{sessionId}}}{RedisKeySuffix}";

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A non-empty session identifier is required.", nameof(sessionId));
        }
    }
}
