using DocRouter.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocRouter.Engine;

public static class CourierRegistration
{
    public static IServiceCollection AddDocRouterCourier(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<CourierOptions>(cfg.GetSection("Courier"));
        services.Configure<DirectoriesOptions>(cfg.GetSection("Dirs"));
        services.Configure<MailOptions>(cfg.GetSection("Mail"));
        services.Configure<PostgresOptions>(cfg.GetSection("Postgres"));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CourierStatus>();
        services.AddSingleton<INotifier, EmailNotifier>();
        services.AddSingleton<ItemRunner>();
        services.AddSingleton<ISourcePoller, ViewSource>();
        services.AddSingleton<ISourcePoller, DirectorySource>();
        services.AddHostedService<CourierWorker>();
        return services;
    }
}
