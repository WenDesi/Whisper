using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using WhisperDesk.Llm.Contract;

namespace WhisperDesk.Llm.Provider.OpenAI;

public class OpenAILlmProvider : ILlmProvider
{
    private readonly ILogger<OpenAILlmProvider> _logger;
    private readonly OpenAILlmConfig _config;

    public string Name => "OpenAI";

    public OpenAILlmProvider(ILogger<OpenAILlmProvider> logger, OpenAILlmConfig config)
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
        _logger.LogInformation("[OpenAI] Processing text ({Length} chars) via {Model}.",
            userText.Length, _config.Model);

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

        _logger.LogInformation("[OpenAI] Response: {InLen} -> {OutLen} chars.",
            userText.Length, result.Length);

        return result;
    }

    public async IAsyncEnumerable<string> ProcessTextStreamingAsync(
        string systemPrompt,
        string userText,
        LlmRequestOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation("[OpenAI] Streaming text ({Length} chars) via {Model}.",
            userText.Length, _config.Model);

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

        await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, chatOptions, ct))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    yield return part.Text;
                }
            }
        }
    }

    private ChatClient CreateClient()
    {
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(_config.Endpoint))
        {
            clientOptions.Endpoint = new Uri(_config.Endpoint);
        }

        var apiKey = string.IsNullOrEmpty(_config.ApiKey) ? "no-key" : _config.ApiKey;

        return new ChatClient(
            credential: new ApiKeyCredential(apiKey),
            model: _config.Model,
            options: clientOptions);
    }
}
