using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhisperDesk.Core.Contract;
using WhisperDesk.Core.Pipeline;
using WhisperDesk.Core.Services;
using WhisperDesk.Core.Stages.PostProcessing;
using WhisperDesk.Core.Stages.PreProcessing;
using WhisperDesk.Core.Tools;
using WhisperDesk.Core.Tools.Shell;

namespace WhisperDesk.Core.Configuration;

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

        // Context providers
        services.AddSingleton<IContextProvider, HotWordContextProvider>();

        if (pipelineConfig.EnableDialogContext)
        {
            services.AddSingleton<IContextProvider, ClaudeDialogContextProvider>();
        }

        // Post-processing stages
        if (pipelineConfig.EnableTextCleanup)
        {
            services.AddSingleton<IPostProcessingStage, LlmTextCleanupStage>();
        }
        services.AddSingleton<IPostProcessingStage, LlmCommandStage>();

        // Local tools (executed in-process, not routed through UI)
        services.AddSingleton<IShellExecutor, WindowsShellExecutor>();
        services.AddSingleton<ILocalTool, ShellExecutionTool>();

        // Pipeline orchestrator
        services.AddSingleton<IPipelineController, StreamingPipeline>();

        return services;
    }
}
