using DocRouter.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocRouter.Engine;

/// <summary>Чистая логика отбора файлов (тестируется без SMB).</summary>
public static class DirectoryDocumentSelector
{
    public static IReadOnlyList<FileEntry> Select(IReadOnlyList<FileEntry> entries, DirectorySourceOptions o, DateTime nowUtc)
    {
        var byName = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) byName.TryAdd(e.Name, e);

        var result = new List<FileEntry>();
        foreach (var e in entries)
        {
            if (e.Name.EndsWith(o.SignatureSuffix, StringComparison.OrdinalIgnoreCase)) continue;       // сами подписи — не документы
            if (o.Extensions.Count > 0 &&
                !o.Extensions.Any(x => e.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
            if (nowUtc - e.LastWriteUtc < o.MinAge) continue;                                           // ещё копируется

            if (o.RequireSignature)
            {
                // Сценарий D: ждём пару; одиночный документ без подписи пока не берём
                if (!byName.TryGetValue(e.Name + o.SignatureSuffix, out var sig)) continue;
                if (nowUtc - sig.LastWriteUtc < o.MinAge) continue;
            }
            result.Add(e);
        }
        return result;
    }
}

/// <summary>Сценарии C и D: файл лежит в Incoming маршрута → документ в работе. Тип — по правилам имени маршрута.</summary>
public sealed class DirectorySource(
    IFileStore files,
    INotifier notifier,
    TimeProvider time,
    IOptions<SourcesOptions> sources,
    IOptions<RoutesOptions> routes,
    ILogger<DirectorySource> log) : ISourcePoller
{
    public string Name => "directories";

    public async Task<IReadOnlyList<WorkItem>> DiscoverAsync(CancellationToken ct)
    {
        var items = new List<WorkItem>();

        foreach (var d in sources.Value.Directories)
        {
            if (!routes.Value.TryGetValue(d.Route, out var route) || string.IsNullOrEmpty(route.Incoming))
            {
                log.LogError("Каталог-источник: у маршрута {Route} нет настроек или Incoming", d.Route);
                continue;
            }

            IReadOnlyList<FileEntry> entries;
            try { entries = await files.ListAsync(route.Incoming, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Не удалось прочитать каталог {Dir}", route.Incoming);
                await notifier.NotifySystemAsync($"[DocRouter] Каталог недоступен: {route.Incoming}", ex.Message, ct);
                continue;
            }

            foreach (var f in DirectoryDocumentSelector.Select(entries, d, time.GetUtcNow().UtcDateTime))
            {
                items.Add(new WorkItem
                {
                    Route = d.Route,
                    DocType = TypeClassifier.Resolve(route, f.Name, null),
                    FileName = f.Name,
                    ExternalId = f.Name,        // для каталога процедуре передаётся имя файла (предположение)
                    Source = SourceKind.Directory,
                    Fields = new Dictionary<string, string> { ["SourcePath"] = f.FullPath }
                });
            }
        }
        return items;
    }
}
