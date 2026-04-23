using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhisperDesk.Llm.Contract;
using WhisperDesk.Llm.Provider.AzureOpenAI;

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
            case "azureopenai":
                var config = new AzureOpenAILlmConfig();
                configuration.GetSection("AzureOpenAI").Bind(config);
                services.AddSingleton(config);
                services.AddSingleton<ILlmProvider, AzureOpenAILlmProvider>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown LLM provider: '{provider}'. Supported: AzureOpenAI");
        }

        return services;
    }
}
