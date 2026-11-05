using DocRouter.Core;
using DocRouter.Engine;
using DocRouter.Host;
using DocRouter.Infrastructure;
using DocRouter.Routes;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);   // v1: только логи в stdout (маршрут, тип, файл, ID — в scope)

builder.Services.AddSingleton(DocumentRoutes.Build());               // маршруты (код)
builder.Services.AddDocRouterCourier(builder.Configuration);         // цикл курьера, источники, письма
builder.Services.AddDocRouterInfrastructure(builder.Configuration);  // SMB, Oracle, REST-подпись, SMTP, штамп

builder.Services.AddHealthChecks()
    .AddCheck<CourierReadinessCheck>("courier", tags: ["ready"])
    .AddCheck<DependenciesHealthCheck>("dependencies", tags: ["deps"]);

var app = builder.Build();

// Валидация конфигурации на старте: ошибка в конфиге должна ронять сервис сразу, а не ночью
var routes = app.Services.GetRequiredService<RouteRegistry>();
var routeCfg = app.Services.GetRequiredService<IOptions<RoutesOptions>>().Value;
var sources = app.Services.GetRequiredService<IOptions<SourcesOptions>>().Value;

foreach (var name in routes.Names)
{
    if (!routeCfg.TryGetValue(name, out var r))
        throw new InvalidOperationException($"Для маршрута '{name}' нет секции Routes в конфигурации");
    if (string.IsNullOrWhiteSpace(r.Incoming) || string.IsNullOrWhiteSpace(r.Result))
        throw new InvalidOperationException($"Маршрут '{name}': должны быть заданы Incoming и Result");
}
foreach (var v in sources.Views)
{
    if (v.Route is null && string.IsNullOrWhiteSpace(v.DocTypeColumn))
        throw new InvalidOperationException($"Вью {v.Name}: задайте Route или DocTypeColumn");
    if (v.Route is not null && !routes.TryGet(v.Route, out _))
        throw new InvalidOperationException($"Вью {v.Name}: неизвестный маршрут '{v.Route}'");
}
foreach (var d in sources.Directories)
    if (!routes.TryGet(d.Route, out _)) throw new InvalidOperationException($"Каталог-источник: неизвестный маршрут '{d.Route}'");

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });                      // процесс жив
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });   // цикл опроса не завис
app.MapHealthChecks("/health/deps", new HealthCheckOptions { Predicate = r => r.Tags.Contains("deps") });     // диагностика: SMB, Oracle (не для probe)

app.Run();
