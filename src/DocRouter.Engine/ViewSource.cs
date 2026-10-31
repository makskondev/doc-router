using DocRouter.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocRouter.Engine;

/// <summary>Опрос Oracle-вью (очередь: запись есть → документ к обработке). Тип: поле вью или фиксированный тип для вью.</summary>
public sealed class ViewSource(
    IViewReader reader,
    RunFactory factory,
    RouteRegistry routes,
    IOptions<SourcesOptions> sources,
    ILogger<ViewSource> log) : ISourcePoller
{
    public string Name => "views";

    public async Task PollAsync(CancellationToken ct)
    {
        foreach (var v in sources.Value.Views)
        {
            IReadOnlyList<ViewRecord> rows;
            try { rows = await reader.ReadAsync(v.Name, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Не удалось прочитать вью {View}", v.Name);
                continue;
            }

            var created = 0;
            foreach (var row in rows)
            {
                var id = row.Get(v.IdColumn);
                var fileName = row.Get(v.FileNameColumn);
                var docType = v.FixedDocType ?? (v.DocTypeColumn is null ? null : row.Get(v.DocTypeColumn));

                if (id is null || fileName is null || docType is null)
                {
                    log.LogWarning("Вью {View}: запись без id/имени файла/типа пропущена", v.Name);
                    continue;
                }
                if (!routes.TryGet(docType, out _))
                {
                    log.LogWarning("Вью {View}: для типа {Type} нет маршрута (id {Id})", v.Name, docType, id);
                    continue;
                }

                var extra = row.Fields.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);
                extra["ExternalId"] = id;
                extra["View"] = v.Name;

                if (await factory.TryCreateAsync(docType, SourceKind.View, $"{v.Name}:{id}", fileName, extra, ct))
                    created++;
            }
            log.LogInformation("Вью {View}: записей {Count}, новых процессов {Created}", v.Name, rows.Count, created);
        }
    }
}
