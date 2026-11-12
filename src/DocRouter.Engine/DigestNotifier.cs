using System.Collections.Concurrent;
using System.Text;
using DocRouter.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocRouter.Engine;

/// <summary>
/// Сводка об ошибках: за опрос копятся все необработанные документы и системные ошибки, в конце опроса (FlushAsync) уходит
/// по одному письму на каждый набор получателей. Получатель документа: группа его типа (NotifyTo) + Mail__Default;
/// системные ошибки — только Mail__Default. Адреса с одинаковым набором ошибок получают одно общее письмо.
/// Частота — раз в опрос, пока проблема остаётся (каждый опрос видит то же самое заново).
/// </summary>
public sealed class DigestNotifier(
    IMailSender mail, IOptions<MailOptions> options, TimeProvider time, ILogger<DigestNotifier> log) : INotifier
{
    internal const int MaxListed = 200;       // сколько документов подробно перечислять в одном письме

    private sealed record Entry(WorkItem? Item, string Step, string Text, string? Title);

    private readonly ConcurrentQueue<Entry> _queue = new();

    public Task NotifyFailureAsync(WorkItem item, string step, string reason, CancellationToken ct)
    {
        _queue.Enqueue(new Entry(item, step, reason, null));
        return Task.CompletedTask;
    }

    public Task NotifySystemAsync(string subject, string body, CancellationToken ct)
    {
        _queue.Enqueue(new Entry(null, "", body, subject));
        return Task.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        var entries = new List<Entry>();
        while (_queue.TryDequeue(out var e)) entries.Add(e);
        if (entries.Count == 0) return;

        var defaults = Recipients.Parse(options.Value.Default);

        // получатель → какие записи ему положены
        var perRecipient = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        var covered = new HashSet<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            var addresses = entries[i].Item is null ? defaults : entries[i].Item!.Recipients.Concat(defaults);
            foreach (var a in addresses.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (!perRecipient.TryGetValue(a, out var set)) perRecipient[a] = set = new SortedSet<int>();
                set.Add(i);
                covered.Add(i);
            }
        }

        if (covered.Count < entries.Count)
            log.LogWarning("Без получателей остались ошибки в количестве {Count}: заполните Mail__Default", entries.Count - covered.Count);

        // одинаковый набор записей → одно письмо на всех таких адресатов
        foreach (var group in perRecipient.GroupBy(kv => string.Join(',', kv.Value), kv => kv.Key))
        {
            var indices = perRecipient[group.First()];
            var subjectAndBody = Compose(entries, indices, time.GetUtcNow().UtcDateTime);
            try
            {
                await mail.SendAsync(group.ToList(), subjectAndBody.Subject, subjectAndBody.Body, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // не страшно: проблемы увидит следующий опрос и сводка уйдёт снова
                log.LogError(ex, "Не удалось отправить сводку: {Subject}", subjectAndBody.Subject);
            }
        }
    }

    private static (string Subject, string Body) Compose(IReadOnlyList<Entry> all, IEnumerable<int> indices, DateTime nowUtc)
    {
        var picked = indices.Select(i => all[i]).ToList();
        var docs = picked.Where(e => e.Item is not null).ToList();
        var system = picked.Where(e => e.Item is null).ToList();

        var subject = docs.Count > 0
            ? $"[DocRouter] Необработанных документов: {docs.Count}" + (system.Count > 0 ? $" (+ системных ошибок: {system.Count})" : "")
            : $"[DocRouter] Системные ошибки: {system.Count}";

        var sb = new StringBuilder();
        sb.AppendLine($"Опрос: {nowUtc:yyyy-MM-dd HH:mm:ss} UTC");

        if (docs.Count > 0)
        {
            sb.AppendLine($"Необработанных документов: {docs.Count}");
            sb.AppendLine();

            var left = MaxListed;
            foreach (var g in docs.GroupBy(e => e.Item!.DocType).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"== {g.Key} ({g.Count()}) ==");
                foreach (var e in g.OrderBy(e => e.Item!.FileName, StringComparer.Ordinal))
                {
                    if (left-- <= 0) break;
                    sb.AppendLine($"• {e.Item!.FileName} (ID {e.Item.ExternalId})");
                    sb.AppendLine($"    шаг: {e.Step}");
                    sb.AppendLine($"    причина: {e.Text}");
                }
                sb.AppendLine();
            }
            if (docs.Count > MaxListed)
                sb.AppendLine($"… и ещё {docs.Count - MaxListed} документов (полный список — в логах сервиса).").AppendLine();
        }

        if (system.Count > 0)
        {
            sb.AppendLine($"== Системные ошибки ({system.Count}) ==");
            foreach (var e in system)
            {
                sb.AppendLine($"• {e.Title}");
                sb.AppendLine($"    {e.Text}");
            }
        }
        return (subject, sb.ToString().TrimEnd());
    }
}
