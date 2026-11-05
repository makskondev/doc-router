using DocRouter.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocRouter.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddDocRouterInfrastructure(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<SmbOptions>(cfg.GetSection("Smb"));
        services.Configure<SignatureServiceOptions>(cfg.GetSection("SignatureService"));
        services.Configure<OracleOptions>(cfg.GetSection("Oracle"));
        services.Configure<SmtpOptions>(cfg.GetSection("Smtp"));

        services.AddSingleton<IFileStore, SmbFileStore>();
        services.AddSingleton<IViewReader, OracleViewReader>();
        services.AddSingleton<ISignatureStore, OracleSignatureStore>();
        services.AddSingleton<IMailSender, SmtpMailSender>();
        services.AddSingleton<IStampRenderer, SkiaStampRenderer>();
        services.AddSingleton<IPdfStamper, PdfSharpStamper>();

        var timeout = cfg.GetSection("SignatureService").GetValue<TimeSpan?>("Timeout") ?? TimeSpan.FromSeconds(60);
        services.AddHttpClient<ISignatureService, RestSignatureService>(c => c.Timeout = timeout);
        return services;
    }
}
