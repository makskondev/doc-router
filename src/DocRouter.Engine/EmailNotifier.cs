using DocRouter.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocRouter.Engine;

/// <summary>Письма отправляются сразу и на каждую неудачу (без ограничения частоты). Сбой SMTP только логируется.</summary>
public sealed class EmailNotifier(IMailSender mail, IOptions<MailOptions> options, ILogger<EmailNotifier> log) : INotifier
{
    public Task NotifyFailureAsync(WorkItem item, string step, string reason, CancellationToken ct)
    {
        var subject = $"[DocRouter] Ошибка: {item.DocType} / {item.FileName}";
        var body = $"""
            Тип документа: {item.DocType}
            Файл: {item.FileName}
            ID: {item.ExternalId}
            Шаг: {step}
            Причина: {reason}
            Время (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}
            """;
        return SendAsync(item.Recipients, subject, body, ct);
    }

    public Task NotifySystemAsync(string subject, string body, CancellationToken ct) =>
        SendAsync([], subject, body, ct);

    private async Task SendAsync(IEnumerable<string> recipients, string subject, string body, CancellationToken ct)
    {
        var to = recipients.Concat(Recipients.Parse(options.Value.Default))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (to.Count == 0)
        {
            log.LogWarning("Нет получателей, письмо не отправлено: {Subject}", subject);
            return;
        }
        try { await mail.SendAsync(to, subject, body, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Не удалось отправить письмо: {Subject}", subject);
        }
    }
}
