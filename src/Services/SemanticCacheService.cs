using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotPluginApi.Configuration;
using CopilotPluginApi.Models;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CopilotPluginApi.Services;

/// <summary>
/// Defines the contract for retrieving and storing semantic cache entries.
/// </summary>
public interface ISemanticCacheService
{
    /// <summary>
    /// Retrieves a cached response for the supplied conversational prompt.
    /// </summary>
    /// <param name="userId">The user identifier associated with the request.</param>
    /// <param name="sessionId">The session identifier used to load conversation history.</param>
    /// <param name="message">The current user message.</param>
    /// <param name="ct">The cancellation token for the operation.</param>
    /// <returns>The cached response when one exists; otherwise, <see langword="null" />.</returns>
    Task<ChatResponse?> GetAsync(string userId, string sessionId, string message, CancellationToken ct = default);

    /// <summary>
    /// Stores a response for the supplied conversational prompt.
    /// </summary>
    /// <param name="userId">The user identifier associated with the request.</param>
    /// <param name="sessionId">The session identifier used to load conversation history.</param>
    /// <param name="message">The current user message.</param>
    /// <param name="response">The response to cache.</param>
    /// <param name="ct">The cancellation token for the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task SetAsync(string userId, string sessionId, string message, ChatResponse response, CancellationToken ct = default);
}

/// <summary>
/// Stores prompt-hash responses in Redis to avoid redundant LLM inference calls.
/// </summary>
/// <param name="connectionMultiplexer">The Redis connection multiplexer.</param>
/// <param name="memoryService">The session memory service used to load conversation history.</param>
/// <param name="promptBuilderService">The prompt builder used to derive the trimmed prompt input.</param>
/// <param name="promptOptions">The configured system prompt.</param>
/// <param name="semanticCacheOptions">The configured semantic cache retention window.</param>
/// <param name="logger">The logger used for non-fatal Redis and serialization failures.</param>
public sealed class SemanticCacheService(
    IConnectionMultiplexer connectionMultiplexer,
    IMemoryService memoryService,
    IPromptBuilderService promptBuilderService,
    IOptions<PromptConfig> promptOptions,
    IOptions<SemanticCacheConfig> semanticCacheOptions,
    ILogger<SemanticCacheService> logger) : ISemanticCacheService
{
    private const string RedisKeyPrefix = "cache:";
    private const string PromptSectionSeparator = "\n\n";
    private const string TurnSectionSeparator = "\n\n";
    private const string TurnRoleContentSeparator = "\n";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDatabase database = connectionMultiplexer.GetDatabase();
    private readonly IMemoryService conversationMemory = memoryService;
    private readonly IPromptBuilderService promptBuilder = promptBuilderService;
    private readonly string systemPrompt = promptOptions.Value.SystemPrompt;
    private readonly TimeSpan timeToLive = TimeSpan.FromHours(semanticCacheOptions.Value.TtlHours);
    private readonly ILogger<SemanticCacheService> serviceLogger = logger;

    /// <inheritdoc />
    public async Task<ChatResponse?> GetAsync(
        string userId,
        string sessionId,
        string message,
        CancellationToken ct = default)
    {
        ValidateIdentifiers(userId, sessionId, message);
        ct.ThrowIfCancellationRequested();

        var key = await BuildRedisKeyAsync(sessionId, message, ct).ConfigureAwait(false);

        try
        {
            var payload = await database.StringGetAsync(key).ConfigureAwait(false);
            if (payload.IsNullOrEmpty)
            {
                return null;
            }

            var cachedResponse = JsonSerializer.Deserialize<ChatResponse>(payload.ToString(), SerializerOptions);
            if (cachedResponse is null)
            {
                return null;
            }

            return CreateCacheHitResponse(cachedResponse);
        }
        catch (RedisException ex)
        {
            serviceLogger.LogWarning(
                ex,
                "Redis semantic cache retrieval failed for user {UserId} and session {SessionId}. Treating the request as a cache miss.",
                userId,
                sessionId);

            return null;
        }
        catch (JsonException ex)
        {
            serviceLogger.LogWarning(
                ex,
                "Failed to deserialize a semantic cache entry for user {UserId} and session {SessionId}. Treating the request as a cache miss.",
                userId,
                sessionId);

            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string userId,
        string sessionId,
        string message,
        ChatResponse response,
        CancellationToken ct = default)
    {
        ValidateIdentifiers(userId, sessionId, message);
        ArgumentNullException.ThrowIfNull(response);
        ct.ThrowIfCancellationRequested();

        var key = await BuildRedisKeyAsync(sessionId, message, ct).ConfigureAwait(false);

        try
        {
            var payload = JsonSerializer.Serialize(response, SerializerOptions);

            await database.StringSetAsync(key, payload, timeToLive).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            serviceLogger.LogWarning(
                ex,
                "Redis semantic cache storage failed for user {UserId} and session {SessionId}. Continuing without cached state.",
                userId,
                sessionId);
        }
        catch (NotSupportedException ex)
        {
            serviceLogger.LogWarning(
                ex,
                "Failed to serialize a semantic cache response for user {UserId} and session {SessionId}. Continuing without cached state.",
                userId,
                sessionId);
        }
    }

    private async Task<RedisKey> BuildRedisKeyAsync(string sessionId, string message, CancellationToken ct)
    {
        var history = await conversationMemory.GetHistoryAsync(sessionId, ct).ConfigureAwait(false);
        var builtPrompt = promptBuilder.Build(systemPrompt, history, message);
        var hashInput = BuildHashInput(builtPrompt);
        var hash = ComputeHash(hashInput);

        return $"{RedisKeyPrefix}{{{hash}}}";
    }

    private static string BuildHashInput(BuiltPrompt builtPrompt)
    {
        var builder = new StringBuilder();
        builder.Append(builtPrompt.SystemPrompt);
        builder.Append(PromptSectionSeparator);

        foreach (var turn in builtPrompt.TrimmedHistory)
        {
            builder.Append(turn.Role);
            builder.Append(TurnRoleContentSeparator);
            builder.Append(turn.Content);
            builder.Append(TurnSectionSeparator);
        }

        builder.Append(builtPrompt.UserMessage);

        return builder.ToString();
    }

    private static string ComputeHash(string input)
    {
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA256.HashData(inputBytes);

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static ChatResponse CreateCacheHitResponse(ChatResponse cachedResponse) =>
        new()
        {
            Response = cachedResponse.Response,
            CacheHit = true,
            Degraded = cachedResponse.Degraded,
            ModelUsed = cachedResponse.ModelUsed,
            PromptTokens = cachedResponse.PromptTokens,
            CompletionTokens = cachedResponse.CompletionTokens,
            LatencyMs = cachedResponse.LatencyMs
        };

    private static void ValidateIdentifiers(string userId, string sessionId, string message)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("A non-empty user identifier is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A non-empty session identifier is required.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A non-empty message is required.", nameof(message));
        }
    }
}
