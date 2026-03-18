using System.ClientModel;
using System.ClientModel.Primitives;
using Azure;
using Azure.AI.OpenAI;
using CopilotPluginApi.Configuration;
using CopilotPluginApi.Models;
using CopilotPluginApi.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OpenAI.Chat;
using Xunit;

namespace CopilotApi.Tests;

/// <summary>
/// Verifies LLM orchestration: retry, fallback, degraded responses, cost accounting, and log safety.
/// </summary>
public sealed class LlmOrchestratorTests
{
    private const string PrimaryDeployment = "gpt-4o";
    private const string FallbackDeployment = "gpt-35-turbo";

    private static readonly CostsConfig TestCosts = new()
    {
        PrimaryInputPerThousand = 0.005,
        PrimaryOutputPerThousand = 0.015,
        FallbackInputPerThousand = 0.0005,
        FallbackOutputPerThousand = 0.0015
    };

    private static AzureOpenAIConfig CreateConfig(int maxRetries = 0) => new()
    {
        Endpoint = "https://test.openai.azure.com/",
        ApiKey = "test-key",
        PrimaryDeployment = PrimaryDeployment,
        FallbackDeployment = FallbackDeployment,
        MaxRetries = maxRetries
    };

    private static BuiltPrompt CreatePrompt(string userMessage = "What is 2 plus 2?") =>
        new("You are a test assistant.", Array.Empty<ConversationTurn>(), userMessage, 10);

    private static LlmOrchestratorService CreateService(
        AzureOpenAIClient client,
        AzureOpenAIConfig? config = null,
        CostsConfig? costs = null,
        ILogger<LlmOrchestratorService>? logger = null) =>
        new(
            client,
            Options.Create(config ?? CreateConfig()),
            Options.Create(costs ?? TestCosts),
            logger ?? Mock.Of<ILogger<LlmOrchestratorService>>());

    private static Mock<ChatClient> CreateSuccessfulChatClient(
        string content, int promptTokens, int completionTokens)
    {
        var contentChunk = CreateStreamingContentUpdate(content);
        var usageChunk = CreateStreamingUsageUpdate(promptTokens, completionTokens);
        var streamingResult = BuildAsyncCollectionResult(contentChunk, usageChunk);

        var mock = new Mock<ChatClient>();
        mock.Setup(c => c.CompleteChatStreamingAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatCompletionOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(streamingResult);
        return mock;
    }

    private static Mock<ChatClient> CreateFailingChatClient(int httpStatusCode)
    {
        var mock = new Mock<ChatClient>();
        mock.Setup(c => c.CompleteChatStreamingAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatCompletionOptions>(),
                It.IsAny<CancellationToken>()))
            .Throws(new RequestFailedException(httpStatusCode, $"Simulated HTTP {httpStatusCode}"));
        return mock;
    }

    private static Mock<AzureOpenAIClient> CreateMockAzureClient(
        params (string deployment, Mock<ChatClient> chatClient)[] routes)
    {
        var mock = new Mock<AzureOpenAIClient>(
            new Uri("https://test.openai.azure.com"),
            new ApiKeyCredential("test-key"));

        foreach (var (deployment, chatClient) in routes)
        {
            mock.Setup(c => c.GetChatClient(deployment)).Returns(chatClient.Object);
        }

        return mock;
    }

    private static StreamingChatCompletionUpdate CreateStreamingContentUpdate(string content)
    {
        var json = $$"""
            {
                "id": "chatcmpl-test",
                "object": "chat.completion.chunk",
                "created": 1700000000,
                "model": "gpt-4o",
                "system_fingerprint": "fp_test",
                "choices": [{"index": 0, "delta": {"content": "{{content}}"}, "logprobs": null, "finish_reason": null}]
            }
            """;
        return ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(json))!;
    }

    private static StreamingChatCompletionUpdate CreateStreamingUsageUpdate(
        int promptTokens, int completionTokens)
    {
        var json = $$"""
            {
                "id": "chatcmpl-test",
                "object": "chat.completion.chunk",
                "created": 1700000000,
                "model": "gpt-4o",
                "system_fingerprint": "fp_test",
                "choices": [],
                "usage": {
                    "prompt_tokens": {{promptTokens}},
                    "completion_tokens": {{completionTokens}},
                    "total_tokens": {{promptTokens + completionTokens}}
                }
            }
            """;
        return ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(json))!;
    }

    private static AsyncCollectionResult<StreamingChatCompletionUpdate> BuildAsyncCollectionResult(
        params StreamingChatCompletionUpdate[] updates) =>
        new TestStreamingResult(updates);

    /// <summary>
    /// Minimal concrete <see cref="AsyncCollectionResult{T}"/> that yields pre-built
    /// <see cref="StreamingChatCompletionUpdate"/> items without requiring a real HTTP pipeline.
    /// </summary>
    private sealed class TestStreamingResult(StreamingChatCompletionUpdate[] items)
        : AsyncCollectionResult<StreamingChatCompletionUpdate>
    {
        private static readonly ClientResult SinglePage =
            ClientResult.FromResponse(new Mock<PipelineResponse>().Object);

        public override ContinuationToken? GetContinuationToken(ClientResult page) => null;

        public override async IAsyncEnumerable<ClientResult> GetRawPagesAsync()
        {
            await Task.CompletedTask;
            yield return SinglePage;
        }

        protected override async IAsyncEnumerable<StreamingChatCompletionUpdate> GetValuesFromPageAsync(
            ClientResult page)
        {
            foreach (var item in items)
            {
                await Task.CompletedTask;
                yield return item;
            }
        }
    }

    [Fact]
    public async Task CompleteAsync_PrimaryModelSucceeds_ReturnsDegradedFalse()
    {
        var primary = CreateSuccessfulChatClient("Hello world", 10, 5);
        var azureClient = CreateMockAzureClient((PrimaryDeployment, primary));
        var service = CreateService(azureClient.Object);

        var result = await service.CompleteAsync(CreatePrompt());

        result.Should().BeEquivalentTo(
            new { Degraded = false, ModelUsed = PrimaryDeployment },
            options => options.Including(r => r.Degraded).Including(r => r.ModelUsed));
    }

    [Fact]
    public async Task CompleteAsync_PrimaryFailsFallbackSucceeds_UsesFallbackModel()
    {
        var primary = CreateFailingChatClient(429);
        var fallback = CreateSuccessfulChatClient("Fallback reply", 10, 5);
        var azureClient = CreateMockAzureClient(
            (PrimaryDeployment, primary),
            (FallbackDeployment, fallback));
        var service = CreateService(azureClient.Object);

        var result = await service.CompleteAsync(CreatePrompt());

        result.Should().BeEquivalentTo(
            new { ModelUsed = FallbackDeployment, Degraded = true },
            options => options.Including(r => r.ModelUsed).Including(r => r.Degraded));
    }

    [Fact]
    public async Task CompleteAsync_BothModelsFail_ReturnsDegradedTrue()
    {
        var primary = CreateFailingChatClient(429);
        var fallback = CreateFailingChatClient(500);
        var azureClient = CreateMockAzureClient(
            (PrimaryDeployment, primary),
            (FallbackDeployment, fallback));
        var service = CreateService(azureClient.Object);

        var result = await service.CompleteAsync(CreatePrompt());

        result.Should().Match<LlmResult>(r => r.Degraded && !string.IsNullOrEmpty(r.Content));
    }

    [Fact]
    public async Task CompleteAsync_RetriesOnlyOn429And503()
    {
        var primary = CreateFailingChatClient(400);
        var fallback = CreateSuccessfulChatClient("ok", 5, 2);
        var azureClient = CreateMockAzureClient(
            (PrimaryDeployment, primary),
            (FallbackDeployment, fallback));
        var service = CreateService(azureClient.Object, CreateConfig(maxRetries: 3));

        await service.CompleteAsync(CreatePrompt());

        primary.Verify(
            c => c.CompleteChatStreamingAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatCompletionOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompleteAsync_CostCalculatedCorrectly()
    {
        const int promptTokens = 100;
        const int completionTokens = 50;
        var primary = CreateSuccessfulChatClient("response", promptTokens, completionTokens);
        var azureClient = CreateMockAzureClient((PrimaryDeployment, primary));
        var service = CreateService(azureClient.Object);
        var expectedCost = Math.Round(
            (promptTokens / 1000.0 * TestCosts.PrimaryInputPerThousand) +
            (completionTokens / 1000.0 * TestCosts.PrimaryOutputPerThousand),
            6, MidpointRounding.AwayFromZero);

        var result = await service.CompleteAsync(CreatePrompt());

        result.CostEstimateUsd.Should().BeApproximately(expectedCost, 1e-6);
    }

    [Fact]
    public async Task CompleteAsync_NeverLogsPromptContent()
    {
        const string sensitivePrompt = "my-credit-card-4111-1111-1111-1111";
        var primary = CreateSuccessfulChatClient("safe response", 10, 5);
        var azureClient = CreateMockAzureClient((PrimaryDeployment, primary));
        var mockLogger = new Mock<ILogger<LlmOrchestratorService>>();
        mockLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var service = CreateService(azureClient.Object, logger: mockLogger.Object);

        await service.CompleteAsync(CreatePrompt(sensitivePrompt));

        var combinedLogOutput = string.Join(
            Environment.NewLine,
            mockLogger.Invocations
                .Where(i => i.Method.Name == "Log")
                .Select(i => i.Arguments[2]?.ToString() ?? string.Empty));
        combinedLogOutput.Should().NotContain(sensitivePrompt);
    }
}
