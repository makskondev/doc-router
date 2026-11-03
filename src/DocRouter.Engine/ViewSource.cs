using DocRouter.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocRouter.Engine;

/// <summary>
/// Опрос Oracle-вью (очередь: запись есть → документ не обработан до конца). Запись исчезает после сохранения подписи.
/// ID файла из вью передаётся хранимой процедуре. Тип: фиксированный для вью → поле вью → правила имени маршрута.
/// </summary>
public sealed class ViewSource(
    IViewReader reader,
    INotifier notifier,
    IOptions<SourcesOptions> sources,
    IOptions<RoutesOptions> routes,
    ILogger<ViewSource> log) : ISourcePoller
{
    public string Name => "views";

    public async Task<IReadOnlyList<WorkItem>> DiscoverAsync(CancellationToken ct)
    {
        var items = new List<WorkItem>();

        foreach (var v in sources.Value.Views)
        {
            if (!routes.Value.TryGetValue(v.Route, out var route))
            {
                log.LogError("Вью {View}: не найден маршрут {Route} в секции Routes", v.Name, v.Route);
                continue;
            }

            IReadOnlyList<ViewRecord> rows;
            try { rows = await reader.ReadAsync(v.Name, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Не удалось прочитать вью {View}", v.Name);
                await notifier.NotifySystemAsync($"[DocRouter] Вью {v.Name} недоступно", ex.Message, ct);
                continue;
            }

            foreach (var row in rows)
            {
                var id = row.Get(v.IdColumn);
                var fileName = row.Get(v.FileNameColumn);
                if (id is null || fileName is null)
                {
                    log.LogWarning("Вью {View}: запись без ID или имени файла пропущена", v.Name);
                    await notifier.NotifySystemAsync($"[DocRouter] Вью {v.Name}: некорректная запись",
                        $"В записи нет {v.IdColumn} или {v.FileNameColumn} (ID={id ?? "—"}, файл={fileName ?? "—"}).", ct);
                    continue;
                }

                var explicitType = v.FixedDocType ?? (v.DocTypeColumn is null ? null : row.Get(v.DocTypeColumn));
                items.Add(new WorkItem
                {
                    Route = v.Route,
                    DocType = TypeClassifier.Resolve(route, fileName, explicitType),
                    FileName = fileName,
                    ExternalId = id,
                    Source = SourceKind.View,
                    Fields = row.Fields.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!)
                });
            }
            log.LogInformation("Вью {View}: записей {Count}", v.Name, rows.Count);
        }
        return items;
    }
}
