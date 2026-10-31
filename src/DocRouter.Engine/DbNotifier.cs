using DocRouter.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocRouter.Engine;

public sealed class DbNotifier(
    DocRouterDb db,
    IOptions<DocumentTypesOptions> types,
    IOptions<EngineOptions> engine,
    ILogger<DbNotifier> log) : INotifier
{
    public Task EnqueueAsync(DocumentRun run, string subject, string body, CancellationToken ct = default)
    {
        var to = new List<string>();
        if (types.Value.TryGetValue(run.DocType, out var t)) to.AddRange(t.ErrorRecipients);
        to.AddRange(engine.Value.DefaultRecipients);
        to = to.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (to.Count == 0)
        {
            log.LogWarning("Нет получателей для типа {Type}, письмо не создано: {Subject}", run.DocType, subject);
            return Task.CompletedTask;
        }

        // Сохраняется вместе с состоянием процесса (SaveChanges в исполнителе)
        db.Notifications.Add(new Notification
        {
            RunId = run.Id,
            Recipients = string.Join(';', to),
            Subject = subject,
            Body = body
        });
        return Task.CompletedTask;
    }
}
