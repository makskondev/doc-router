using DocRouter.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocRouter.Engine;

/// <summary>Забирает готовые процессы и выполняет их.</summary>
public sealed class EngineWorker(
    RunClaimer claimer, RunExecutor executor, IOptions<EngineOptions> options, ILogger<EngineWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        using var timer = new PeriodicTimer(o.TickInterval);
        try
        {
            do
            {
                try
                {
                    var ids = await claimer.ClaimAsync(ct);
                    if (ids.Count == 0) continue;

                    await Parallel.ForEachAsync(ids,
                        new ParallelOptions { MaxDegreeOfParallelism = o.MaxParallel, CancellationToken = ct },
                        async (id, c) =>
                        {
                            try { await executor.ExecuteAsync(id, c); }
                            catch (OperationCanceledException) when (c.IsCancellationRequested) { }
                            catch (Exception ex) { log.LogError(ex, "Ошибка исполнителя для процесса {RunId}", id); }
                        });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Ошибка цикла движка");
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Периодический опрос источников. В k8s опрашивает только один под (advisory lock).</summary>
public sealed class SourcePollerWorker(
    IEnumerable<ISourcePoller> pollers,
    IServiceScopeFactory scopes,
    IOptions<EngineOptions> options,
    ILogger<SourcePollerWorker> log) : BackgroundService
{
    private const long LockKey = 7_301_001;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.Value.PollInterval);
        try
        {
            do
            {
                try { await PollOnceAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Ошибка опроса источников");
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        string? cs;
        await using (var scope = scopes.CreateAsyncScope())
            cs = scope.ServiceProvider.GetRequiredService<DocRouterDb>().Database.GetConnectionString();

        await using var lease = await PgAdvisoryLock.TryAcquireAsync(cs!, LockKey, ct);
        if (lease is null) return;   // опрашивает другой под

        foreach (var p in pollers)
        {
            try { await p.PollAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Источник {Source} завершился ошибкой", p.Name);
            }
        }
    }
}

/// <summary>Отправка писем из outbox. Сбой SMTP не влияет на обработку документов.</summary>
public sealed class NotificationSenderWorker(
    IServiceScopeFactory scopes, IMailSender mail, ILogger<NotificationSenderWorker> log) : BackgroundService
{
    private const int MaxAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                try { await SendPendingAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Ошибка отправки писем");
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendPendingAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocRouterDb>();

        var batch = await db.Notifications
            .Where(n => n.Status == NotificationStatus.Pending)
            .OrderBy(n => n.Id).Take(50).ToListAsync(ct);

        foreach (var n in batch)
        {
            try
            {
                await mail.SendAsync(n.Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries), n.Subject, n.Body, ct);
                n.Status = NotificationStatus.Sent;
                n.SentAt = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                n.Attempts++;
                n.LastError = ex.Message;
                if (n.Attempts >= MaxAttempts) n.Status = NotificationStatus.Failed;
                log.LogWarning(ex, "Письмо {Id} не отправлено (попытка {Attempt})", n.Id, n.Attempts);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
