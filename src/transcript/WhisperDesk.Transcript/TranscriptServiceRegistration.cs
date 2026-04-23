using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisperDesk.Transcript.Contract;
using WhisperDesk.Transcript.Services;

namespace WhisperDesk.Transcript;

public static class TranscriptServiceRegistration
{
    public static IServiceCollection AddTranscriptServices(
        this IServiceCollection services,
        double sessionGapMinutes = 10)
    {
        services.AddSingleton<ITranscriptionHistoryService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<TranscriptionHistoryService>>();
            var gap = TimeSpan.FromMinutes(sessionGapMinutes);
            return new TranscriptionHistoryService(logger, gap);
        });

        return services;
    }
}
