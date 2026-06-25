using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using WhisperDesk.Llm.Contract;

namespace WhisperDesk.Llm.Provider.Doubao;

public class DoubaoLlmProvider : ILlmProvider
{
    private readonly ILogger<DoubaoLlmProvider> _logger;
    private readonly DoubaoLlmConfig _config;
    private readonly ChatClient _client;
    private int _warmingUp;

    public string Name => "Doubao";

    public DoubaoLlmProvider(ILogger<DoubaoLlmProvider> logger, DoubaoLlmConfig config)
    {
        _logger = logger;
        _config = config;
        _client = CreateClient();
    }

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        // Skip if a warm-up is already in flight (rapid press/release shouldn't pile up requests).
        if (Interlocked.CompareExchange(ref _warmingUp, 1, 0) != 0) return;

        try
        {
            var messages = new List<ChatMessage> { new UserChatMessage("hi") };
            var options = new ChatCompletionOptions { MaxOutputTokenCount = 1 };
            await _client.CompleteChatAsync(messages, options, ct);
            _logger.LogDebug("[Doubao] Connection warmed up.");
        }
        catch (Exception ex)
        {
            // Warm-up is best-effort — never let it affect the real session.
            _logger.LogDebug(ex, "[Doubao] Warm-up failed (non-fatal).");
        }
        finally
        {
            Interlocked.Exchange(ref _warmingUp, 0);
        }
    }

    public async Task<string> ProcessTextAsync(
        string systemPrompt,
        string userText,
        LlmRequestOptions? options = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[Doubao] Processing text ({Length} chars) via {Model}.",
            userText.Length, _config.Model);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userText)
        };

        var chatOptions = BuildChatOptions(options);

        var response = await _client.CompleteChatAsync(messages, chatOptions, ct);
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

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userText)
        };

        var chatOptions = BuildChatOptions(options);

        await foreach (var update in _client.CompleteChatStreamingAsync(messages, chatOptions, ct))
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
        if (toolContext.Tools.Count > 0)
        {
            chatOptions.ToolChoice = ChatToolChoice.CreateAutoChoice();
        }

        while (true)
        {
            var response = await _client.CompleteChatAsync(messages, chatOptions, ct);
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

        // Doubao/Ark defaults to "thinking" mode, which adds 3-5s of first-token latency.
        // Cleanup is a trivial task that needs no reasoning, so disable it via a body-injection
        // policy (the OpenAI SDK has no strongly-typed field for this Ark-specific param).
        clientOptions.AddPolicy(new ThinkingDisabledPolicy(), PipelinePosition.PerCall);

        var apiKey = string.IsNullOrEmpty(_config.ApiKey) ? "no-key" : _config.ApiKey;

        return new ChatClient(
            credential: new ApiKeyCredential(apiKey),
            model: _config.Model,
            options: clientOptions);
    }

    /// <summary>
    /// Injects <c>"thinking": { "type": "disabled" }</c> into every chat request body so the
    /// Doubao model skips its reasoning phase and starts emitting output immediately.
    /// </summary>
    private sealed class ThinkingDisabledPolicy : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
        {
            Inject(message);
            ProcessNext(message, pipeline, index);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
        {
            Inject(message);
            await ProcessNextAsync(message, pipeline, index);
        }

        private static void Inject(PipelineMessage message)
        {
            if (message.Request?.Content is null) return;

            using var stream = new MemoryStream();
            message.Request.Content.WriteTo(stream);
            stream.Position = 0;

            if (JsonNode.Parse(stream) is not JsonObject body) return;

            body["thinking"] = new JsonObject { ["type"] = "disabled" };
            message.Request.Content = BinaryContent.Create(BinaryData.FromString(body.ToJsonString()));
        }
    }
}
