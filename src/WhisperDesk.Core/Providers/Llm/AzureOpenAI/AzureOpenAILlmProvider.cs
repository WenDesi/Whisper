using System.ClientModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using WhisperDesk.Core.Diagnostics;

namespace WhisperDesk.Core.Providers.Llm.AzureOpenAI;

/// <summary>
/// Azure OpenAI LLM provider. Uses the OpenAI SDK with Azure endpoint.
/// </summary>
public class AzureOpenAILlmProvider : ILlmProvider
{
    private readonly ILogger<AzureOpenAILlmProvider> _logger;
    private readonly AzureOpenAILlmConfig _config;

    public string Name => "Azure OpenAI";

    public AzureOpenAILlmProvider(ILogger<AzureOpenAILlmProvider> logger, AzureOpenAILlmConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task<string> ProcessTextAsync(
        string systemPrompt,
        string userText,
        LlmRequestOptions? options = null,
        CancellationToken ct = default)
    {
        using var activity = DiagnosticSources.Llm.StartActivity("AzureOpenAI.ProcessText");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("llm.model", _config.ChatDeployment);
        activity?.SetTag("llm.input.length", userText.Length);

        _logger.LogInformation("[AzureOpenAI] Processing text ({Length} chars) via {Model}.",
            userText.Length, _config.ChatDeployment);

        var chatClient = CreateClient();
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userText)
        };

        var chatOptions = new ChatCompletionOptions();
        if (options?.Temperature is float temp)
        {
            chatOptions.Temperature = temp;
        }
        if (options?.MaxTokens is int maxTokens)
        {
            chatOptions.MaxOutputTokenCount = maxTokens;
        }

        var response = await chatClient.CompleteChatAsync(messages, chatOptions, ct);
        var result = response.Value.Content[0].Text;

        _logger.LogInformation("[AzureOpenAI] Response: {InLen} -> {OutLen} chars.",
            userText.Length, result.Length);

        activity?.SetTag("llm.output.length", result.Length);

        return result;
    }

    public async IAsyncEnumerable<string> ProcessTextStreamingAsync(
        string systemPrompt,
        string userText,
        LlmRequestOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var activity = DiagnosticSources.Llm.StartActivity("AzureOpenAI.ProcessTextStreaming");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);
        activity?.SetTag("llm.model", _config.ChatDeployment);
        activity?.SetTag("llm.input.length", userText.Length);

        _logger.LogInformation("[AzureOpenAI] Streaming text ({Length} chars) via {Model}.",
            userText.Length, _config.ChatDeployment);

        var chatClient = CreateClient();
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userText)
        };

        var chatOptions = new ChatCompletionOptions();
        if (options?.Temperature is float temp)
        {
            chatOptions.Temperature = temp;
        }

        int chunkCount = 0;
        await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, chatOptions, ct))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    chunkCount++;
                    yield return part.Text;
                }
            }
        }

        activity?.SetTag("llm.stream.chunks", chunkCount);
    }

    private ChatClient CreateClient()
    {
        return new ChatClient(
            credential: new ApiKeyCredential(_config.ApiKey),
            model: _config.ChatDeployment,
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(_config.Endpoint)
            });
    }
}
