using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhisperDesk.Core.Pipeline;
using WhisperDesk.Core.Providers.Llm;
using WhisperDesk.Core.Providers.Llm.AzureOpenAI;
using WhisperDesk.Core.Providers.Stt;
using WhisperDesk.Core.Providers.Stt.Azure;
using WhisperDesk.Core.Providers.Stt.Volcengine;
using WhisperDesk.Core.Services;
using WhisperDesk.Core.Stages.PostProcessing;
using WhisperDesk.Core.Stages.PreProcessing;

namespace WhisperDesk.Core.Configuration;

/// <summary>
/// DI registration for WhisperDesk pipeline services.
/// Call services.AddWhisperDeskPipeline(config, configuration) from the host application.
/// </summary>
public static class PipelineServiceRegistration
{
    public static IServiceCollection AddWhisperDeskPipeline(
        this IServiceCollection services,
        PipelineConfig pipelineConfig,
        IConfiguration configuration)
    {
        System.Diagnostics.Debug.WriteLine($"[PipelineRegistration] Registering pipeline services...");

        services.AddSingleton(pipelineConfig);

        // Audio routing
        services.AddSingleton<AudioRouter>();
        System.Diagnostics.Debug.WriteLine($"[PipelineRegistration] Registered AudioRouter");

        // STT provider
        services.AddSttProvider(pipelineConfig.SttProvider, configuration);

        // LLM provider
        services.AddLlmProvider(pipelineConfig.LlmProvider, configuration);

        // Context providers
        services.AddSingleton<IContextProvider, HotWordContextProvider>();
        System.Diagnostics.Debug.WriteLine($"[PipelineRegistration] Registered HotWordContextProvider");

        // Post-processing stages
        if (pipelineConfig.EnableTextCleanup)
        {
            services.AddSingleton<IPostProcessingStage, LlmTextCleanupStage>();
            System.Diagnostics.Debug.WriteLine($"[PipelineRegistration] Registered LlmTextCleanupStage");
        }

        // Logging service
        services.AddSingleton<TranscriptionLogService>();

        // Pipeline orchestrator
        services.AddSingleton<IPipelineController, StreamingPipeline>();
        System.Diagnostics.Debug.WriteLine($"[PipelineRegistration] Pipeline registration complete");

        return services;
    }

    private static void AddSttProvider(this IServiceCollection services, string provider, IConfiguration configuration)
    {
        System.Diagnostics.Debug.WriteLine($"[PipelineRegistration] Registering STT provider: {provider}");

        switch (provider.ToLowerInvariant())
        {
            case "azurespeech":
                services.BindAndRegister<AzureSttConfig>(configuration, "AzureSpeech");
                services.AddSingleton<IStreamingSttProvider, AzureSttProvider>();
                System.Diagnostics.Debug.WriteLine($"[PipelineRegistration] Registered AzureSttProvider");
                break;
            case "volcengine":
                services.BindAndRegister<VolcengineSttConfig>(configuration, "VolcengineSpeech");
                services.AddSingleton<IStreamingSttProvider, VolcengineSttProvider>();
                System.Diagnostics.Debug.WriteLine($"[PipelineRegistration] Registered VolcengineSttProvider");
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown STT provider: '{provider}'. Supported: AzureSpeech, Volcengine");
        }
    }

    private static void AddLlmProvider(this IServiceCollection services, string provider, IConfiguration configuration)
    {
        System.Diagnostics.Debug.WriteLine($"[PipelineRegistration] Registering LLM provider: {provider}");

        switch (provider.ToLowerInvariant())
        {
            case "azureopenai":
                services.BindAndRegister<AzureOpenAILlmConfig>(configuration, "AzureOpenAI");
                services.AddSingleton<ILlmProvider, AzureOpenAILlmProvider>();
                System.Diagnostics.Debug.WriteLine($"[PipelineRegistration] Registered AzureOpenAILlmProvider");
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
