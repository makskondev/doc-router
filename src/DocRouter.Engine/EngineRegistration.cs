using DocRouter.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocRouter.Engine;

public static class EngineRegistration
{
    public static IServiceCollection AddDocRouterEngine(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<EngineOptions>(cfg.GetSection("Engine"));
        services.Configure<DocumentTypesOptions>(cfg.GetSection("DocumentTypes"));
        services.Configure<SourcesOptions>(cfg.GetSection("Sources"));

        services.AddDbContext<DocRouterDb>(o => o
            .UseNpgsql(cfg.GetConnectionString("Postgres"))
            .UseSnakeCaseNamingConvention());

        services.AddScoped<INotifier, DbNotifier>();
        services.AddSingleton<RunFactory>();
        services.AddSingleton<RunClaimer>();
        services.AddSingleton<RunExecutor>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISourcePoller, ViewSource>();
        services.AddSingleton<ISourcePoller, DirectorySource>();

        services.AddHostedService<EngineWorker>();
        services.AddHostedService<SourcePollerWorker>();
        services.AddHostedService<NotificationSenderWorker>();
        return services;
    }
}
