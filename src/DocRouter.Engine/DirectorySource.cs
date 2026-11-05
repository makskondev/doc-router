using DocRouter.Core;
using Microsoft.Extensions.Logging;

namespace DocRouter.Engine;

/// <summary>Чистая логика отбора файлов (тестируется без SMB).</summary>
public static class DirectoryDocumentSelector
{
    public static IReadOnlyList<FileEntry> Select(IReadOnlyList<FileEntry> entries, DirectorySourceDef d, DateTime nowUtc)
    {
        var byName = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) byName.TryAdd(e.Name, e);

        var result = new List<FileEntry>();
        foreach (var e in entries)
        {
            if (e.Name.EndsWith(d.SignatureSuffix, StringComparison.OrdinalIgnoreCase)) continue;      // сами подписи — не документы
            if (!string.IsNullOrWhiteSpace(d.Mask) && !FileMask.IsMatch(d.Mask, e.Name)) continue;      // файл другого типа в общей папке
            if (nowUtc - e.LastWriteUtc < d.MinAge) continue;                                          // ещё копируется

            if (d.WithSignature)
            {
                // Подпись уже поставлена: ждём пару; одиночный документ без подписи пока не берём
                if (!byName.TryGetValue(e.Name + d.SignatureSuffix, out var sig)) continue;
                if (nowUtc - sig.LastWriteUtc < d.MinAge) continue;
            }
            result.Add(e);
        }
        return result;
    }
}

/// <summary>
/// Типы с источником-папкой: файл лежит в папке (и подходит под mask) → документ в работе. Одну папку могут читать несколько типов:
/// файлы разделяются масками. Файл, подходящий сразу нескольким типам, не обрабатывается — вместо этого системное письмо.
/// </summary>
public sealed class DirectorySource(
    IFileStore files,
    INotifier notifier,
    TimeProvider time,
    DocumentTypeRegistry types,
    ILogger<DirectorySource> log) : ISourcePoller
{
    public string Name => "directories";

    public async Task<IReadOnlyList<WorkItem>> DiscoverAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var listings = new Dictionary<string, IReadOnlyList<FileEntry>?>(StringComparer.Ordinal);   // один листинг на папку за опрос
        var claims = new List<(DocumentType Type, FileEntry File)>();

        foreach (var t in types.All.Where(t => t.Directory is not null))
        {
            var def = t.Directory!;
            var key = Paths.NormalizeDir(def.Directory);
            if (!listings.TryGetValue(key, out var entries))
            {
                try { entries = await files.ListAsync(def.Directory, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Не удалось прочитать папку {Dir}", def.Directory);
                    await notifier.NotifySystemAsync($"[DocRouter] Папка недоступна: {def.Directory}", ex.Message, ct);
                    entries = null;
                }
                listings[key] = entries;
            }
            if (entries is null) continue;

            foreach (var f in DirectoryDocumentSelector.Select(entries, def, now))
                claims.Add((t, f));
        }

        var items = new List<WorkItem>();
        foreach (var g in claims.GroupBy(c => (Dir: Paths.NormalizeDir(c.Type.Directory!.Directory), Name: c.File.Name.ToLowerInvariant())))
        {
            var claimants = g.Select(c => c.Type.Name).Distinct().ToList();
            if (claimants.Count > 1)
            {
                var first = g.First();
                log.LogError("Файл {File} в {Dir} подходит нескольким типам: {Types}", first.File.Name, first.Type.Directory!.Directory, claimants);
                await notifier.NotifySystemAsync($"[DocRouter] Файл подходит нескольким типам: {first.File.Name}",
                    $"Папка {first.Type.Directory!.Directory}, файл {first.File.Name} заявлен типами: {string.Join(", ", claimants)}. Уточните mask в описании типов.", ct);
                continue;
            }

            var c = g.First();
            items.Add(new WorkItem
            {
                DocType = c.Type.Name,
                FileName = c.File.Name,
                ExternalId = c.File.Name,        // для папки процедуре передаётся имя файла (предположение)
                Source = SourceKind.Directory,
                Recipients = c.Type.Recipients,
                Fields = new Dictionary<string, string> { ["SourcePath"] = c.File.FullPath }
            });
        }
        return items;
    }
}
