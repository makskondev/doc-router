using DocRouter.Core;
using Microsoft.Extensions.Logging;

namespace DocRouter.Engine;

/// <summary>
/// Опрос Oracle-вью (очередь: запись есть → документ не обработан до конца; запись исчезает после сохранения подписи).
/// Каждое вью читается один раз за опрос. Во вью могут быть несколько типов и коды, которых сервис не обрабатывает:
/// такие записи пропускаются (без писем), сводка по пропущенным кодам пишется в лог.
/// ID файла из вью передаётся хранимой процедуре.
/// </summary>
public sealed class ViewSource(
    IViewReader reader,
    INotifier notifier,
    DocumentTypeRegistry types,
    ILogger<ViewSource> log) : ISourcePoller
{
    public string Name => "views";

    public async Task<IReadOnlyList<WorkItem>> DiscoverAsync(CancellationToken ct)
    {
        var items = new List<WorkItem>();

        foreach (var group in types.All.Where(t => t.View is not null).GroupBy(t => t.View!.View, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<ViewRecord> rows;
            try { rows = await reader.ReadAsync(group.Key, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Не удалось прочитать вью {View}", group.Key);
                await notifier.NotifySystemAsync($"[DocRouter] Вью {group.Key} недоступно", ex.Message, ct);
                continue;
            }

            var skipped = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var handled = false;
                foreach (var t in group)
                {
                    var def = t.View!;
                    var code = row.Get(def.TypeColumn)?.Trim();
                    if (code is null || !string.Equals(code, def.Code, StringComparison.OrdinalIgnoreCase)) continue;

                    handled = true;
                    var id = row.Get(def.IdColumn);
                    var fileName = row.Get(def.NameColumn);
                    if (id is null || fileName is null)
                    {
                        log.LogWarning("Вью {View}: запись типа {Type} без ID или имени файла пропущена", group.Key, t.Name);
                        await notifier.NotifySystemAsync($"[DocRouter] Вью {group.Key}: некорректная запись",
                            $"Тип {t.Name}: ID={id ?? "—"}, файл={fileName ?? "—"} (колонки {def.IdColumn}, {def.NameColumn}).", ct);
                        continue;
                    }

                    items.Add(new WorkItem
                    {
                        DocType = t.Name,
                        FileName = fileName,
                        ExternalId = id,
                        Source = SourceKind.View,
                        Recipients = t.Recipients,
                        Fields = row.Fields.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!)
                    });
                }

                if (!handled)
                {
                    var key = row.Get(group.First().View!.TypeColumn)?.Trim() ?? "(пусто)";
                    skipped[key] = skipped.GetValueOrDefault(key) + 1;
                }
            }

            log.LogInformation("Вью {View}: записей {Count}, чужих типов пропущено {Skipped}", group.Key, rows.Count, skipped.Values.Sum());
            if (skipped.Count > 0)
                log.LogDebug("Вью {View}: пропущенные коды {Codes}", group.Key, string.Join(", ", skipped.Select(kv => $"{kv.Key}×{kv.Value}")));
        }
        return items;
    }
}
