using DocRouter.Core;
using DocRouter.Engine;
using DocRouter.Host;
using DocRouter.Infrastructure;
using DocRouter.Routes;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole();   // v1: стандартный ILogger в stdout, без Serilog/OpenTelemetry

builder.Services.AddSingleton(DocumentRoutes.Build());              // маршруты (код)
builder.Services.AddDocRouterEngine(builder.Configuration);         // PostgreSQL, исполнитель, воркеры
builder.Services.AddDocRouterInfrastructure(builder.Configuration); // SMB, Oracle, REST-подпись, SMTP

builder.Services.AddHealthChecks()
    .AddDbContextCheck<DocRouterDb>("postgres", tags: ["ready"])
    .AddCheck<DependenciesHealthCheck>("dependencies", tags: ["deps"]);

var app = builder.Build();

// Валидация конфигурации на старте: ошибка в конфиге должна ронять сервис сразу, а не ночью
var routes = app.Services.GetRequiredService<RouteRegistry>();
var types = app.Services.GetRequiredService<IOptions<DocumentTypesOptions>>().Value;
foreach (var type in routes.DocTypes)
{
    if (!types.TryGetValue(type, out var t))
        throw new InvalidOperationException($"Для типа '{type}' нет секции DocumentTypes в конфигурации");
    if (string.IsNullOrWhiteSpace(t.Incoming) || string.IsNullOrWhiteSpace(t.Result))
        throw new InvalidOperationException($"Тип '{type}': должны быть заданы Incoming и Result");
}

if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DocRouterDb>().Database.MigrateAsync();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });                    // процесс жив
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") }); // только PostgreSQL
app.MapHealthChecks("/health/deps", new HealthCheckOptions { Predicate = r => r.Tags.Contains("deps") });   // диагностика: SMB, Oracle (не для probe)

app.Run();
