using DocRouter.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocRouter.Engine;

/// <summary>
/// Проходит маршрут документа с первого шага. Состояние не сохраняется: Next → следующий шаг, Wait → тихо до следующего опроса,
/// Fail/исключение → лог + письмо, повтор на следующем опросе.
/// </summary>
public sealed class ItemRunner(
    IServiceProvider services,
    RouteRegistry routes,
    IOptions<RoutesOptions> routeOptions,
    INotifier notifier,
    ILogger<ItemRunner> log)
{
    public async Task RunAsync(WorkItem item, CancellationToken ct)
    {
        using var scope = log.BeginScope(new Dictionary<string, object?>
        {
            ["route"] = item.Route, ["file"] = item.FileName, ["id"] = item.ExternalId
        });

        if (!routes.TryGet(item.Route, out var route) || !routeOptions.Value.TryGetValue(item.Route, out var routeCfg))
        {
            await FailAsync(item, "-", $"Для маршрута '{item.Route}' нет описания в коде или настроек в секции Routes", ct);
            return;
        }

        var ctx = new StepContext
        {
            Item = item,
            State = new ItemState { FileName = item.FileName },
            Route = routeCfg,
            Logger = log
        };

        foreach (var def in route.Steps)
        {
            ct.ThrowIfCancellationRequested();

            var step = (IStep)ActivatorUtilities.CreateInstance(services, def.StepType);
            def.Configure?.Invoke(step);

            StepResult result;
            try
            {
                result = await step.ExecuteAsync(ctx, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Шаг {Step} завершился исключением", def.Name);
                result = StepResult.Fail($"{ex.GetType().Name}: {ex.Message}");
            }

            switch (result)
            {
                case NextResult:
                    continue;

                case WaitResult w:
                    log.LogInformation("Шаг {Step}: ожидание ({Reason})", def.Name, w.Reason);
                    return;

                case FailResult f:
                    await FailAsync(item, def.Name, f.Reason, ct);
                    return;
            }
        }

        log.LogInformation("Документ обработан до конца маршрута");
    }

    private async Task FailAsync(WorkItem item, string step, string reason, CancellationToken ct)
    {
        log.LogError("Шаг {Step}: {Reason}", step, reason);
        await notifier.NotifyFailureAsync(item, step, reason, ct);
    }
}
