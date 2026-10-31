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

/// <summary>Сценарии C и D: файл появился в Incoming типа → процесс (ключ = полный путь).</summary>
public sealed class DirectorySource(
    IFileStore files,
    RunFactory factory,
    RouteRegistry routes,
    TimeProvider time,
    IOptions<SourcesOptions> sources,
    IOptions<DocumentTypesOptions> types,
    ILogger<DirectorySource> log) : ISourcePoller
{
    public string Name => "directories";

    public async Task PollAsync(CancellationToken ct)
    {
        foreach (var d in sources.Value.Directories)
        {
            if (!routes.TryGet(d.DocType, out _) || !types.Value.TryGetValue(d.DocType, out var t) || string.IsNullOrEmpty(t.Incoming))
            {
                log.LogWarning("Каталог-источник: тип {Type} без маршрута или без Incoming, пропущен", d.DocType);
                continue;
            }

            IReadOnlyList<FileEntry> entries;
            try { entries = await files.ListAsync(t.Incoming, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Не удалось прочитать каталог {Dir} (тип {Type})", t.Incoming, d.DocType);
                continue;
            }

            var created = 0;
            foreach (var f in DirectoryDocumentSelector.Select(entries, d, time.GetUtcNow().UtcDateTime))
            {
                var extra = new Dictionary<string, string> { ["ExternalId"] = f.Name, ["SourcePath"] = f.FullPath };
                if (await factory.TryCreateAsync(d.DocType, SourceKind.Directory, f.FullPath, f.Name, extra, ct)) created++;
            }
            if (created > 0) log.LogInformation("Каталог {Dir}: новых процессов {Created}", t.Incoming, created);
        }
    }
}
