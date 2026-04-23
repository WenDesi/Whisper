using Microsoft.Extensions.DependencyInjection;
using WhisperDesk.Jobs.HotwordLearning;

namespace WhisperDesk.Jobs;

public static class JobsServiceRegistration
{
    public static IServiceCollection AddOfflineJobs(this IServiceCollection services)
    {
        services.AddHostedService<HotwordLearningJob>();
        return services;
    }
}
