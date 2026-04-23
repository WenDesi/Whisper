using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhisperDesk.Stt.Contract;
using WhisperDesk.Stt.Azure;
using WhisperDesk.Stt.Volcengine;

namespace WhisperDesk.Stt;

public static class SttServiceRegistration
{
    public static IServiceCollection AddSttProvider(
        this IServiceCollection services,
        string provider,
        IConfiguration configuration)
    {
        switch (provider.ToLowerInvariant())
        {
            case "azurespeech":
                var azureConfig = new AzureSttConfig();
                configuration.GetSection("AzureSpeech").Bind(azureConfig);
                services.AddSingleton(azureConfig);
                services.AddSingleton<IStreamingSttProvider, AzureSttProvider>();
                break;
            case "volcengine":
                var volcConfig = new VolcengineSttConfig();
                configuration.GetSection("VolcengineSpeech").Bind(volcConfig);
                services.AddSingleton(volcConfig);
                services.AddSingleton<IStreamingSttProvider, VolcengineSttProvider>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown STT provider: '{provider}'. Supported: AzureSpeech, Volcengine");
        }

        return services;
    }
}
