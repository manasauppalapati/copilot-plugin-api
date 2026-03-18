using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Azure;
using Azure.AI.OpenAI;
using CopilotPluginApi.Configuration;
using CopilotPluginApi.Models;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace CopilotPluginApi.Services;

/// <summary>
/// Defines the contract for completing prompts through the configured LLM pipeline.
/// </summary>
public interface ILlmOrchestratorService
{
    /// <summary>
    /// Completes the supplied prompt and returns the aggregated model output.
    /// </summary>
    /// <param name="prompt">The fully assembled prompt to execute.</param>
    /// <param name="ct">The cancellation token for the operation.</param>
    /// <returns>The completed LLM result, including token usage and cost metadata.</returns>
    Task<LlmResult> CompleteAsync(BuiltPrompt prompt, CancellationToken ct = default);
}

/// <summary>
/// Executes chat completions against Azure OpenAI with retry, fallback, streaming, and cost accounting.
/// </summary>
/// <param name="azureOpenAIClient">The Azure OpenAI client.</param>
/// <param name="azureOpenAIOptions">The configured Azure OpenAI endpoint and deployment settings.</param>
/// <param name="costsOptions">The configured model pricing rates.</param>
/// <param name="logger">The logger used for retry, fallback, and degraded-path diagnostics.</param>
public sealed class LlmOrchestratorService(
    AzureOpenAIClient azureOpenAIClient,
    IOptions<AzureOpenAIConfig> azureOpenAIOptions,
    IOptions<CostsConfig> costsOptions,
    ILogger<LlmOrchestratorService> logger) : ILlmOrchestratorService
{
    private const string NoModelUsed = "none";
    private const string DegradedContent = "Service is temporarily limited. Please try again shortly.";
    private const int RetryableStatusTooManyRequests = 429;
    private const int RetryableStatusServiceUnavailable = 503;

    private readonly AzureOpenAIClient openAIClient = azureOpenAIClient;
    private readonly AzureOpenAIConfig openAIConfig = azureOpenAIOptions.Value;
    private readonly CostsConfig pricingConfig = costsOptions.Value;
    private readonly ILogger<LlmOrchestratorService> serviceLogger = logger;

    /// <inheritdoc />
    public async Task<LlmResult> CompleteAsync(BuiltPrompt prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var executionSummary = await ExecuteWithRetryAndFallbackAsync(
            prompt,
            static (_, _) => ValueTask.CompletedTask,
            ct).ConfigureAwait(false);

        return new LlmResult(
            executionSummary.Content,
            executionSummary.ModelUsed,
            executionSummary.PromptTokens,
            executionSummary.CompletionTokens,
            executionSummary.Degraded,
            executionSummary.CostEstimateUsd);
    }

    /// <summary>
    /// Streams completion chunks for the supplied prompt as they arrive from Azure OpenAI.
    /// </summary>
    /// <param name="prompt">The fully assembled prompt to execute.</param>
    /// <param name="ct">The cancellation token for the operation.</param>
    /// <returns>An asynchronous sequence of streamed text chunks.</returns>
    public async IAsyncEnumerable<string> StreamAsync(
        BuiltPrompt prompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        var producerTask = ProduceStreamAsync(prompt, channel.Writer, ct);

        await foreach (var chunk in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return chunk;
        }

        await producerTask.ConfigureAwait(false);
    }

    private async Task ProduceStreamAsync(
        BuiltPrompt prompt,
        ChannelWriter<string> writer,
        CancellationToken ct)
    {
        var wroteChunk = false;

        try
        {
            var executionSummary = await ExecuteWithRetryAndFallbackAsync(
                prompt,
                async (chunk, token) =>
                {
                    wroteChunk = true;
                    await writer.WriteAsync(chunk, token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);

            if (executionSummary.Degraded && !wroteChunk)
            {
                await writer.WriteAsync(executionSummary.Content, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task<StreamingExecutionSummary> ExecuteWithRetryAndFallbackAsync(
        BuiltPrompt prompt,
        Func<string, CancellationToken, ValueTask> forwardChunkAsync,
        CancellationToken ct)
    {
        var chatMessages = BuildChatMessages(prompt);
        Exception? primaryFailure = null;
        var attemptedPrimaryCount = 0;

        // Production hardening: replace with Polly ResiliencePipeline
        for (var attempt = 0; attempt <= openAIConfig.MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            attemptedPrimaryCount = attempt + 1;
            serviceLogger.LogInformation(
                "Attempting LLM completion with model {ModelUsed} on attempt {AttemptNumber}.",
                openAIConfig.PrimaryDeployment,
                attemptedPrimaryCount);

            try
            {
                return await InvokeModelAsync(
                    openAIConfig.PrimaryDeployment,
                    isDegraded: false,
                    chatMessages,
                    prompt.TotalTokens,
                    forwardChunkAsync,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (TryGetRetryableStatusCode(ex, out var statusCode) && attempt < openAIConfig.MaxRetries)
            {
                primaryFailure = ex;

                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                serviceLogger.LogWarning(
                    ex,
                    "Retrying model {ModelUsed} after attempt {AttemptNumber} because the service returned status {StatusCode}. Waiting {Delay}.",
                    openAIConfig.PrimaryDeployment,
                    attemptedPrimaryCount,
                    statusCode,
                    delay);

                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                primaryFailure = ex;
                break;
            }
        }

        serviceLogger.LogWarning(
            primaryFailure,
            "Primary model {PrimaryModel} failed after {AttemptCount} attempt(s). Activating fallback model {FallbackModel}.",
            openAIConfig.PrimaryDeployment,
            attemptedPrimaryCount,
            openAIConfig.FallbackDeployment);

        try
        {
            serviceLogger.LogInformation(
                "Attempting LLM completion with model {ModelUsed} on attempt {AttemptNumber}.",
                openAIConfig.FallbackDeployment,
                1);

            return await InvokeModelAsync(
                openAIConfig.FallbackDeployment,
                isDegraded: true,
                chatMessages,
                prompt.TotalTokens,
                forwardChunkAsync,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            serviceLogger.LogError(
                ex,
                "Both LLM models failed. Returning a degraded response.");

            return CreateDegradedSummary();
        }
    }

    private async Task<StreamingExecutionSummary> InvokeModelAsync(
        string deploymentName,
        bool isDegraded,
        IReadOnlyList<ChatMessage> messages,
        int fallbackPromptTokenCount,
        Func<string, CancellationToken, ValueTask> forwardChunkAsync,
        CancellationToken ct)
    {
        var contentBuilder = new StringBuilder();
        var refusalBuilder = new StringBuilder();
        ChatTokenUsage? usage = null;
        var chatClient = openAIClient.GetChatClient(deploymentName);
        var completionOptions = new ChatCompletionOptions();
        var updates = chatClient.CompleteChatStreamingAsync(messages, completionOptions, ct);

        await foreach (var update in updates.WithCancellation(ct).ConfigureAwait(false))
        {
            usage = update.Usage ?? usage;

            foreach (var contentPart in update.ContentUpdate)
            {
                if (contentPart.Kind is not ChatMessageContentPartKind.Text || string.IsNullOrEmpty(contentPart.Text))
                {
                    continue;
                }

                contentBuilder.Append(contentPart.Text);
                await forwardChunkAsync(contentPart.Text, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(update.RefusalUpdate))
            {
                refusalBuilder.Append(update.RefusalUpdate);
            }
        }

        var content = contentBuilder.Length > 0
            ? contentBuilder.ToString()
            : refusalBuilder.ToString();

        var promptTokens = usage?.InputTokenCount ?? fallbackPromptTokenCount;
        var completionTokens = usage?.OutputTokenCount ?? 0;
        var costEstimate = CalculateCostEstimate(deploymentName, promptTokens, completionTokens);

        return new StreamingExecutionSummary(
            content,
            deploymentName,
            promptTokens,
            completionTokens,
            isDegraded,
            costEstimate);
    }

    private List<ChatMessage> BuildChatMessages(BuiltPrompt prompt)
    {
        var messages = new List<ChatMessage>(prompt.TrimmedHistory.Count + 2)
        {
            new SystemChatMessage(prompt.SystemPrompt)
        };

        foreach (var turn in prompt.TrimmedHistory)
        {
            messages.Add(CreateChatMessage(turn));
        }

        messages.Add(new UserChatMessage(prompt.UserMessage));

        return messages;
    }

    private static ChatMessage CreateChatMessage(ConversationTurn turn) =>
        turn.Role switch
        {
            "user" => new UserChatMessage(turn.Content),
            "assistant" => new AssistantChatMessage(turn.Content),
            _ => throw new InvalidOperationException(
                $"Unsupported conversation role '{turn.Role}'.")
        };

    private double CalculateCostEstimate(string modelUsed, int promptTokens, int completionTokens)
    {
        var (inputRate, outputRate) = modelUsed switch
        {
            var value when value.Equals(openAIConfig.PrimaryDeployment, StringComparison.OrdinalIgnoreCase) =>
                (pricingConfig.PrimaryInputPerThousand, pricingConfig.PrimaryOutputPerThousand),
            var value when value.Equals(openAIConfig.FallbackDeployment, StringComparison.OrdinalIgnoreCase) =>
                (pricingConfig.FallbackInputPerThousand, pricingConfig.FallbackOutputPerThousand),
            _ => (0D, 0D)
        };

        var cost = ((promptTokens / 1000D) * inputRate) + ((completionTokens / 1000D) * outputRate);
        return Math.Round(cost, 6, MidpointRounding.AwayFromZero);
    }

    private static bool TryGetRetryableStatusCode(Exception exception, out int statusCode)
    {
        switch (exception)
        {
            case RequestFailedException { Status: RetryableStatusTooManyRequests or RetryableStatusServiceUnavailable } requestFailedException:
                statusCode = requestFailedException.Status;
                return true;
            case ClientResultException { Status: RetryableStatusTooManyRequests or RetryableStatusServiceUnavailable } clientResultException:
                statusCode = clientResultException.Status;
                return true;
            default:
                statusCode = 0;
                return false;
        }
    }

    private static StreamingExecutionSummary CreateDegradedSummary() =>
        new(
            DegradedContent,
            NoModelUsed,
            0,
            0,
            true,
            0D);

    private sealed record StreamingExecutionSummary(
        string Content,
        string ModelUsed,
        int PromptTokens,
        int CompletionTokens,
        bool Degraded,
        double CostEstimateUsd);
}
