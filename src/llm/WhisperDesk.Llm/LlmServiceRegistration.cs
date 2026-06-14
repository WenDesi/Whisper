using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhisperDesk.Llm.Contract;
using WhisperDesk.Llm.Provider.Doubao;
using WhisperDesk.Llm.Provider.OpenAI;

namespace WhisperDesk.Llm;

public static class LlmServiceRegistration
{
    public static IServiceCollection AddLlmProvider(
        this IServiceCollection services,
        string provider,
        IConfiguration configuration)
    {
        switch (provider.ToLowerInvariant())
        {
            case "openai":
                var openAiConfig = new OpenAILlmConfig();
                configuration.GetSection("Llm").Bind(openAiConfig);
                services.AddSingleton(openAiConfig);
                services.AddSingleton<ILlmProvider, OpenAILlmProvider>();
                break;
            case "doubao":
                var doubaoConfig = new DoubaoLlmConfig();
                configuration.GetSection("Doubao").Bind(doubaoConfig);
                services.AddSingleton(doubaoConfig);
                services.AddSingleton<ILlmProvider, DoubaoLlmProvider>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown LLM provider: '{provider}'. Supported: OpenAI, Doubao");
        }

        return services;
    }
}
