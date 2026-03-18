using CopilotPluginApi.Configuration;
using CopilotPluginApi.Models;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

namespace CopilotPluginApi.Services;

/// <summary>
/// Defines the contract for assembling prompts within the configured token budget.
/// </summary>
public interface IPromptBuilderService
{
    /// <summary>
    /// Builds a prompt using the supplied system prompt, conversation history, and current user message.
    /// </summary>
    /// <param name="systemPrompt">The fixed system prompt prepended to the conversation.</param>
    /// <param name="history">The conversation history ordered from oldest to newest.</param>
    /// <param name="userMessage">The current user message.</param>
    /// <returns>The fully assembled prompt components and their exact token count.</returns>
    BuiltPrompt Build(string systemPrompt, IReadOnlyList<ConversationTurn> history, string userMessage);
}

/// <summary>
/// Represents a fully assembled prompt that is ready for model invocation.
/// </summary>
/// <param name="SystemPrompt">The fixed system prompt for the conversation.</param>
/// <param name="TrimmedHistory">The trimmed conversation history that fits within the token budget.</param>
/// <param name="UserMessage">The current user message.</param>
/// <param name="TotalTokens">The exact total token count of the assembled prompt.</param>
public sealed record BuiltPrompt(
    string SystemPrompt,
    IReadOnlyList<ConversationTurn> TrimmedHistory,
    string UserMessage,
    int TotalTokens);

/// <summary>
/// Assembles prompts deterministically within the configured token budget.
/// </summary>
/// <param name="memoryOptions">The configured memory token budget.</param>
/// <param name="tokenizer">The tokenizer used for exact token counting.</param>
public sealed class PromptBuilderService(
    IOptions<MemoryConfig> memoryOptions,
    Tokenizer tokenizer) : IPromptBuilderService
{
    private readonly int tokenBudget = memoryOptions.Value.TokenBudget;
    private readonly Tokenizer promptTokenizer = tokenizer;

    /// <inheritdoc />
    public BuiltPrompt Build(string systemPrompt, IReadOnlyList<ConversationTurn> history, string userMessage)
    {
        ValidateText(systemPrompt, nameof(systemPrompt), "A non-empty system prompt is required.");
        ArgumentNullException.ThrowIfNull(history);
        ValidateText(userMessage, nameof(userMessage), "A non-empty user message is required.");

        var systemPromptTokens = promptTokenizer.CountTokens(systemPrompt);
        var userMessageTokens = promptTokenizer.CountTokens(userMessage);

        if (userMessageTokens > tokenBudget)
        {
            throw new PromptTooLargeException(
                $"The user message consumes {userMessageTokens} tokens, which exceeds the configured prompt budget of {tokenBudget} tokens.");
        }

        var nonHistoryTokenCount = systemPromptTokens + userMessageTokens;
        if (nonHistoryTokenCount > tokenBudget)
        {
            throw new PromptTooLargeException(
                $"The system prompt and user message consume {nonHistoryTokenCount} tokens, which exceeds the configured prompt budget of {tokenBudget} tokens.");
        }

        var historyBudget = tokenBudget - nonHistoryTokenCount;
        var trimmedHistory = PromptTokenCalculator.TrimToTokenBudget(promptTokenizer, history, historyBudget);
        var historyTokens = PromptTokenCalculator.CountHistoryTokens(promptTokenizer, trimmedHistory);

        return new BuiltPrompt(
            systemPrompt,
            trimmedHistory,
            userMessage,
            nonHistoryTokenCount + historyTokens);
    }

    private static void ValidateText(string value, string parameterName, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(message, parameterName);
        }
    }
}

internal static class PromptTokenCalculator
{
    private const string TurnTokenSeparator = "\n";

    internal static IReadOnlyList<ConversationTurn> TrimToTokenBudget(
        Tokenizer tokenizer,
        IReadOnlyList<ConversationTurn> history,
        int tokenBudget)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(history);

        if (tokenBudget < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenBudget), tokenBudget, "The token budget must be zero or greater.");
        }

        if (history.Count == 0 || tokenBudget == 0)
        {
            return Array.Empty<ConversationTurn>();
        }

        var tokenCounts = new int[history.Count];
        long totalTokens = 0;

        for (var i = 0; i < history.Count; i++)
        {
            var turn = history[i] ?? throw new InvalidOperationException("Conversation history cannot contain null turns.");
            var tokenCount = CountConversationTurnTokens(tokenizer, turn);

            tokenCounts[i] = tokenCount;
            totalTokens += tokenCount;
        }

        if (totalTokens <= tokenBudget)
        {
            return history;
        }

        var startIndex = 0;
        while (startIndex < history.Count && totalTokens > tokenBudget)
        {
            totalTokens -= tokenCounts[startIndex];
            startIndex++;
        }

        if (startIndex >= history.Count)
        {
            return Array.Empty<ConversationTurn>();
        }

        var trimmedHistory = new ConversationTurn[history.Count - startIndex];
        for (var i = startIndex; i < history.Count; i++)
        {
            trimmedHistory[i - startIndex] = history[i];
        }

        return trimmedHistory;
    }

    internal static int CountHistoryTokens(Tokenizer tokenizer, IReadOnlyList<ConversationTurn> history)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(history);

        long totalTokens = 0;

        for (var i = 0; i < history.Count; i++)
        {
            var turn = history[i] ?? throw new InvalidOperationException("Conversation history cannot contain null turns.");
            totalTokens += CountConversationTurnTokens(tokenizer, turn);
        }

        return checked((int)totalTokens);
    }

    internal static int CountConversationTurnTokens(Tokenizer tokenizer, ConversationTurn turn)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(turn);

        return tokenizer.CountTokens($"{turn.Role}{TurnTokenSeparator}{turn.Content}");
    }
}
