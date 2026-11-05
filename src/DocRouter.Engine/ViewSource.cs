using DocRouter.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocRouter.Engine;

/// <summary>
/// Опрос Oracle-вью (очередь: запись есть → документ не обработан до конца). Запись исчезает после сохранения подписи.
/// Маршрут (= тип документа): ViewOptions.Route для всего вью, иначе значение колонки DocTypeColumn.
/// ID файла из вью передаётся хранимой процедуре.
/// </summary>
public sealed class ViewSource(
    IViewReader reader,
    INotifier notifier,
    RouteRegistry routes,
    IOptions<SourcesOptions> sources,
    ILogger<ViewSource> log) : ISourcePoller
{
    public string Name => "views";

    public async Task<IReadOnlyList<WorkItem>> DiscoverAsync(CancellationToken ct)
    {
        var items = new List<WorkItem>();

        foreach (var v in sources.Value.Views)
        {
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
                var route = v.Route ?? (v.DocTypeColumn is null ? null : row.Get(v.DocTypeColumn));

                if (id is null || fileName is null || route is null)
                {
                    log.LogWarning("Вью {View}: запись без ID, имени файла или типа пропущена", v.Name);
                    await notifier.NotifySystemAsync($"[DocRouter] Вью {v.Name}: некорректная запись",
                        $"ID={id ?? "—"}, файл={fileName ?? "—"}, тип={route ?? "—"} (колонки {v.IdColumn}, {v.FileNameColumn}, {v.DocTypeColumn}).", ct);
                    continue;
                }
                if (!routes.TryGet(route, out _))
                {
                    log.LogWarning("Вью {View}: для типа {Type} нет маршрута (ID {Id})", v.Name, route, id);
                    await notifier.NotifySystemAsync($"[DocRouter] Нет маршрута для типа документа {route}",
                        $"Вью {v.Name}, ID={id}, файл={fileName}: тип '{route}' не описан в DocumentRoutes.", ct);
                    continue;
                }

                items.Add(new WorkItem
                {
                    Route = route,
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
