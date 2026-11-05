using DocRouter.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocRouter.Engine;

/// <summary>
/// Опрос Oracle-вью (очередь: запись есть → документ не обработан до конца). Запись исчезает после сохранения подписи.
/// Маршрут (= тип документа): ViewOptions.Route для всего вью, иначе код из колонки DocTypeColumn через TypeMap.
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

    /// <summary>Код типа → имя маршрута: по TypeMap (строго) или, если словаря нет, код = имя маршрута.</summary>
    internal static string? Resolve(ViewOptions v, string code)
    {
        if (v.TypeMap.Count == 0) return code;
        foreach (var kv in v.TypeMap)
            if (string.Equals(kv.Key.Trim(), code, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

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
                if (id is null || fileName is null)
                {
                    log.LogWarning("Вью {View}: запись без ID или имени файла пропущена", v.Name);
                    await notifier.NotifySystemAsync($"[DocRouter] Вью {v.Name}: некорректная запись",
                        $"ID={id ?? "—"}, файл={fileName ?? "—"} (колонки {v.IdColumn}, {v.FileNameColumn}).", ct);
                    continue;
                }

                var route = v.Route;
                if (route is null)
                {
                    var code = v.DocTypeColumn is null ? null : row.Get(v.DocTypeColumn)?.Trim();
                    if (code is null)
                    {
                        log.LogWarning("Вью {View}: у записи {Id} нет кода типа", v.Name, id);
                        await notifier.NotifySystemAsync($"[DocRouter] Вью {v.Name}: нет типа документа",
                            $"ID={id}, файл={fileName}: пуста колонка {v.DocTypeColumn}.", ct);
                        continue;
                    }

                    route = Resolve(v, code);
                    if (route is null)
                    {
                        log.LogWarning("Вью {View}: код типа {Code} не сопоставлен с маршрутом (ID {Id})", v.Name, code, id);
                        await notifier.NotifySystemAsync($"[DocRouter] Неизвестный код типа документа: {code}",
                            $"Вью {v.Name}, ID={id}, файл={fileName}: код '{code}' отсутствует в Sources:Views:TypeMap.", ct);
                        continue;
                    }
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
