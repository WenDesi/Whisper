using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using WhisperDesk.Llm.Contract;

namespace WhisperDesk.Llm.Provider.Doubao;

public class DoubaoLlmProvider : ILlmProvider
{
    private readonly ILogger<DoubaoLlmProvider> _logger;
    private readonly DoubaoLlmConfig _config;

    public string Name => "Doubao";

    public DoubaoLlmProvider(ILogger<DoubaoLlmProvider> logger, DoubaoLlmConfig config)
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
        _logger.LogInformation("[Doubao] Processing text ({Length} chars) via {Model}.",
            userText.Length, _config.Model);

        var chatClient = CreateClient();
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userText)
        };

        var chatOptions = BuildChatOptions(options);

        var response = await chatClient.CompleteChatAsync(messages, chatOptions, ct);
        var result = response.Value.Content[0].Text;

        _logger.LogInformation("[Doubao] Response: {InLen} -> {OutLen} chars.",
            userText.Length, result.Length);

        return result;
    }

    public async IAsyncEnumerable<string> ProcessTextStreamingAsync(
        string systemPrompt,
        string userText,
        LlmRequestOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation("[Doubao] Streaming text ({Length} chars) via {Model}.",
            userText.Length, _config.Model);

        var chatClient = CreateClient();
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userText)
        };

        var chatOptions = BuildChatOptions(options);

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

    public async Task<string> ProcessCommandAsync(
        string systemPrompt,
        string userText,
        ToolContext toolContext,
        LlmRequestOptions? options = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[Doubao] Starting agent loop ({Length} chars, {ToolCount} tools) via {Model}.",
            userText.Length, toolContext.Tools.Count, _config.Model);

        var chatClient = CreateClient();
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userText)
        };

        var chatOptions = BuildChatOptions(options);
        chatOptions.Tools.Clear();
        foreach (var tool in toolContext.Tools)
        {
            chatOptions.Tools.Add(ChatTool.CreateFunctionTool(
                tool.Name,
                tool.Description,
                BinaryData.FromString(tool.ParametersSchema)));
        }
        chatOptions.ToolChoice = ChatToolChoice.CreateAutoChoice();

        while (true)
        {
            var response = await chatClient.CompleteChatAsync(messages, chatOptions, ct);
            var completion = response.Value;

            _logger.LogInformation("[Doubao] Turn {Turn}: FinishReason={FinishReason}, ContentCount={ContentCount}, ToolCallCount={ToolCallCount}",
                messages.Count, completion.FinishReason, completion.Content.Count, completion.ToolCalls.Count);

            messages.Add(new AssistantChatMessage(completion));

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                foreach (var toolCall in completion.ToolCalls)
                {
                    var arguments = toolCall.FunctionArguments.ToString();
                    _logger.LogDebug("[Doubao] Tool call: {Tool}({Args})", toolCall.FunctionName, arguments);

                    var toolResult = await toolContext.ToolExecutor(toolCall.FunctionName, arguments, ct);

                    _logger.LogDebug("[Doubao] Tool result: {Result}", toolResult);
                    messages.Add(new ToolChatMessage(toolCall.Id, toolResult));
                }

                continue;
            }

            var finalText = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;
            _logger.LogInformation("[Doubao] Agent loop done after {Turns} messages, FinishReason={FinishReason}, OutLen={OutLen}.",
                messages.Count, completion.FinishReason, finalText.Length);
            return finalText;
        }
    }

    private static ChatCompletionOptions BuildChatOptions(LlmRequestOptions? options)
    {
        var chatOptions = new ChatCompletionOptions();
        if (options?.Temperature is float temp)
            chatOptions.Temperature = temp;
        if (options?.MaxTokens is int maxTokens)
            chatOptions.MaxOutputTokenCount = maxTokens;
        return chatOptions;
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
