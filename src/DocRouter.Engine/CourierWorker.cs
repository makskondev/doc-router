using DocRouter.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocRouter.Engine;

/// <summary>Цикл курьера: опросили источники → прошли маршрут по каждому документу → ждём следующий опрос. Запуски не накладываются.</summary>
public sealed class CourierWorker(
    IEnumerable<ISourcePoller> sources,
    ItemRunner runner,
    INotifier notifier,
    CourierStatus status,
    TimeProvider time,
    IOptions<CourierOptions> options,
    ILogger<CourierWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.Value.PollInterval);
        try
        {
            do
            {
                try
                {
                    await CycleAsync(ct);
                    status.MarkCycle(time.GetUtcNow().UtcDateTime);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Ошибка цикла опроса");
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Один опрос. В конце (даже при сбое) уходит сводка: одно письмо на набор получателей вместо письма на каждый файл.</summary>
    internal async Task CycleAsync(CancellationToken ct)
    {
        try
        {
            var items = new List<WorkItem>();
            foreach (var s in sources)
            {
                try { items.AddRange(await s.DiscoverAsync(ct)); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Источник {Source} завершился ошибкой", s.Name);
                    await notifier.NotifySystemAsync($"[DocRouter] Источник {s.Name} завершился ошибкой", ex.ToString(), ct);
                }
            }

            var unique = items.GroupBy(i => (i.DocType, i.ExternalId)).Select(g => g.First()).ToList();
            log.LogInformation("Опрос: документов в работе {Count}", unique.Count);

            await Parallel.ForEachAsync(unique,
                new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxParallel, CancellationToken = ct },
                async (item, c) =>
                {
                    try { await runner.RunAsync(item, c); }
                    catch (OperationCanceledException) when (c.IsCancellationRequested) { }
                    catch (Exception ex) { log.LogError(ex, "Необработанная ошибка документа {Item}", item); }
                });
        }
        finally
        {
            await notifier.FlushAsync(ct);
        }
    }
}
