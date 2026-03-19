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
/// Call services.AddWhisperDeskPipeline(config) from the host application.
/// </summary>
public static class PipelineServiceRegistration
{
    public static IServiceCollection AddWhisperDeskPipeline(
        this IServiceCollection services,
        PipelineConfig pipelineConfig,
        AzureSttConfig azureSttConfig,
        AzureOpenAILlmConfig azureOpenAIConfig,
        VolcengineSttConfig? volcengineSttConfig = null)
    {
        // Configuration
        services.AddSingleton(pipelineConfig);
        services.AddSingleton(azureSttConfig);
        services.AddSingleton(azureOpenAIConfig);
        if (volcengineSttConfig != null)
        {
            services.AddSingleton(volcengineSttConfig);
        }

        // Audio routing
        services.AddSingleton<AudioRouter>();

        // STT provider (keyed by config)
        switch (pipelineConfig.SttProvider.ToLowerInvariant())
        {
            case "azurespeech":
                services.AddSingleton<IStreamingSttProvider, AzureSttProvider>();
                break;
            case "volcengine":
                if (volcengineSttConfig == null)
                    throw new InvalidOperationException(
                        "Volcengine STT provider selected but VolcengineSpeech configuration is missing from appsettings.json.");
                services.AddSingleton<IStreamingSttProvider, VolcengineSttProvider>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown STT provider: '{pipelineConfig.SttProvider}'. Supported: AzureSpeech, Volcengine");
        }

        // LLM provider (keyed by config)
        switch (pipelineConfig.LlmProvider.ToLowerInvariant())
        {
            case "azureopenai":
                services.AddSingleton<ILlmProvider, AzureOpenAILlmProvider>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown LLM provider: '{pipelineConfig.LlmProvider}'. Supported: AzureOpenAI");
        }

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
}
