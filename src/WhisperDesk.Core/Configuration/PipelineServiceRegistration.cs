using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhisperDesk.Core.Pipeline;
using WhisperDesk.Core.Providers.Llm;
using WhisperDesk.Core.Providers.Llm.AzureOpenAI;
using WhisperDesk.Core.Services;
using WhisperDesk.Core.Stages.PostProcessing;
using WhisperDesk.Core.Stages.PreProcessing;

namespace WhisperDesk.Core.Configuration;

/// <summary>
/// DI registration for WhisperDesk pipeline services.
/// STT provider registration is handled by WhisperDesk.Stt.SttServiceRegistration.
/// Call services.AddSttProvider(...) from WhisperDesk.Stt before calling this.
/// </summary>
public static class PipelineServiceRegistration
{
    public static IServiceCollection AddWhisperDeskPipeline(
        this IServiceCollection services,
        PipelineConfig pipelineConfig,
        IConfiguration configuration)
    {
        services.AddSingleton(pipelineConfig);

        // Audio device enumeration + level metering
        services.AddSingleton<AudioDeviceService>();

        // Audio routing
        services.AddSingleton<AudioRouter>();

        // LLM provider
        services.AddLlmProvider(pipelineConfig.LlmProvider, configuration);

        // Context providers
        services.AddSingleton<IContextProvider, HotWordContextProvider>();

        // Post-processing stages
        if (pipelineConfig.EnableTextCleanup)
        {
            services.AddSingleton<IPostProcessingStage, LlmTextCleanupStage>();
        }

        // Logging service
        services.AddSingleton<TranscriptionLogService>();

        // Pipeline orchestrator
        services.AddSingleton<IPipelineController, StreamingPipeline>();

        return services;
    }

    private static void AddLlmProvider(this IServiceCollection services, string provider, IConfiguration configuration)
    {
        switch (provider.ToLowerInvariant())
        {
            case "azureopenai":
                services.BindAndRegister<AzureOpenAILlmConfig>(configuration, "AzureOpenAI");
                services.AddSingleton<ILlmProvider, AzureOpenAILlmProvider>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown LLM provider: '{provider}'. Supported: AzureOpenAI");
        }
    }

    /// <summary>
    /// Binds a configuration section to a new instance and registers it as a singleton.
    /// </summary>
    private static T BindAndRegister<T>(this IServiceCollection services, IConfiguration configuration, string sectionName)
        where T : class, new()
    {
        var config = new T();
        configuration.GetSection(sectionName).Bind(config);
        services.AddSingleton(config);
        return config;
    }
}
