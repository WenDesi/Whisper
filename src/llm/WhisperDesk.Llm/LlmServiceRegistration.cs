using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhisperDesk.Llm.Contract;
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
                var config = new OpenAILlmConfig();
                configuration.GetSection("Llm").Bind(config);
                services.AddSingleton(config);
                services.AddSingleton<ILlmProvider, OpenAILlmProvider>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown LLM provider: '{provider}'. Supported: OpenAI");
        }

        return services;
    }
}
