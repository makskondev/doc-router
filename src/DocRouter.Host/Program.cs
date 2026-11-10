using DocRouter.Core;
using DocRouter.Engine;
using DocRouter.Host;
using DocRouter.Infrastructure;
using DocRouter.Routes;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);   // v1: только логи в stdout (тип, файл, ID — в scope)

builder.Services.AddDocRouterCourier(builder.Configuration);         // цикл курьера, источники, письма
builder.Services.AddDocRouterInfrastructure(builder.Configuration);  // SMB, Oracle, PostgreSQL-архив подписей, REST-подпись, SMTP

// Типы документов описаны в коде; рабочие папки и группы адресатов приходят из окружения (Dirs__*, Mail__*)
builder.Services.AddSingleton(sp => DocumentDefinitions.Build(
    sp.GetRequiredService<IOptions<DirectoriesOptions>>().Value,
    sp.GetRequiredService<IOptions<MailOptions>>().Value));

builder.Services.AddHealthChecks()
    .AddCheck<CourierReadinessCheck>("courier", tags: ["ready"])
    .AddCheck<DependenciesHealthCheck>("dependencies", tags: ["deps"]);

var app = builder.Build();

// Fail-fast на старте: сначала настройки окружения, потом описание типов
var missing = ConfigCheck.MissingSettings(
    app.Services.GetRequiredService<IOptions<DirectoriesOptions>>().Value,
    app.Services.GetRequiredService<IOptions<MailOptions>>().Value,
    app.Services.GetRequiredService<IOptions<PostgresOptions>>().Value);
if (missing.Count > 0)
    throw new InvalidOperationException("Не хватает настроек окружения:\n" + string.Join("\n", missing));

_ = app.Services.GetRequiredService<DocumentTypeRegistry>();   // проверка описания типов (источники, шаги, дубли, маски)

// Схема архива подписей (одна таблица, CREATE TABLE IF NOT EXISTS). Без доступного PostgreSQL сервис не стартует.
await app.Services.GetRequiredService<ISignatureArchive>().EnsureSchemaAsync(app.Lifetime.ApplicationStopping);

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });                      // процесс жив
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });   // цикл опроса не завис
app.MapHealthChecks("/health/deps", new HealthCheckOptions { Predicate = r => r.Tags.Contains("deps") });     // диагностика: SMB, Oracle (не для probe)

app.Run();
