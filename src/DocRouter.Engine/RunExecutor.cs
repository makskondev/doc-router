using DocRouter.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocRouter.Engine;

/// <summary>Выполняет шаги процесса подряд, пока не встретится Wait / Transient / Fail / конец маршрута.</summary>
public sealed class RunExecutor(
    IServiceScopeFactory scopes,
    RouteRegistry routes,
    IOptions<EngineOptions> engine,
    IOptions<DocumentTypesOptions> types,
    ILogger<RunExecutor> log)
{
    public async Task ExecuteAsync(Guid runId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<DocRouterDb>();
        var run = await db.Runs.FirstAsync(r => r.Id == runId, ct);

        try
        {
            await RunStepsAsync(db, sp, run, ct);
        }
        finally
        {
            // Снимаем аренду в любом случае (при остановке сервиса шаг просто повторится — шаги идемпотентны)
            run.LockedBy = null;
            run.LockedUntil = null;
            run.UpdatedAt = DateTime.UtcNow;
            run.FlushContext();
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception ex) { log.LogError(ex, "Не удалось сохранить процесс {RunId}", run.Id); }
        }
    }

    private async Task RunStepsAsync(DocRouterDb db, IServiceProvider sp, DocumentRun run, CancellationToken ct)
    {
        var eng = engine.Value;

        if (!routes.TryGet(run.DocType, out var route) || !types.Value.TryGetValue(run.DocType, out var typeOpts))
        {
            await FailAsync(db, sp, run, $"Для типа '{run.DocType}' нет маршрута или настроек", ct);
            return;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (run.StepIndex >= route.Steps.Count)
            {
                run.Status = RunStatus.Completed;
                run.CompletedAt = DateTime.UtcNow;
                run.Error = null;
                return;
            }

            var def = route.Steps[run.StepIndex];
            if (run.StepName != def.Name)
            {
                // Маршрут изменили деплоем: ищем текущий шаг по имени
                var idx = route.IndexOf(run.StepName);
                if (idx < 0)
                {
                    await FailAsync(db, sp, run,
                        $"Шаг '{run.StepName}' отсутствует в маршруте '{run.DocType}' (изменён деплоем?)", ct);
                    return;
                }
                run.StepIndex = idx;
                def = route.Steps[idx];
            }

            run.LockedUntil = DateTime.UtcNow + eng.LeaseDuration;   // продлеваем аренду

            var step = (IStep)ActivatorUtilities.CreateInstance(sp, def.StepType);
            def.Configure?.Invoke(step);

            var ctx = new StepContext
            {
                Run = run,
                State = run.Context,
                Type = typeOpts,
                PollInterval = eng.PollInterval,
                Logger = log
            };

            var started = DateTime.UtcNow;
            StepResult result;
            try
            {
                result = await step.ExecuteAsync(ctx, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Шаг {Step} процесса {RunId} завершился исключением", def.Name, run.Id);
                result = StepResult.Transient($"{ex.GetType().Name}: {ex.Message}");
            }

            db.StepLogs.Add(new RunStepLog
            {
                RunId = run.Id,
                StepIndex = run.StepIndex,
                StepName = def.Name,
                StartedAt = started,
                FinishedAt = DateTime.UtcNow,
                Outcome = result.GetType().Name.Replace("Result", ""),
                Error = result switch { TransientResult t => t.Reason, FailResult f => f.Reason, _ => null }
            });

            switch (result)
            {
                case NextResult:
                    run.StepIndex++;
                    run.StepName = run.StepIndex < route.Steps.Count ? route.Steps[run.StepIndex].Name : "";
                    run.Attempt = 0;
                    run.Error = null;
                    run.FlushContext();
                    await db.SaveChangesAsync(ct);   // состояние фиксируется после каждого шага
                    continue;

                case WaitResult w:
                    run.Status = RunStatus.Waiting;
                    run.NextAttemptAt = DateTime.UtcNow + w.Delay;
                    run.FlushContext();
                    await db.SaveChangesAsync(ct);
                    return;

                case TransientResult t:
                    run.Attempt++;
                    run.Error = t.Reason;
                    if (run.Attempt > eng.MaxTransientAttempts)
                    {
                        await FailAsync(db, sp, run, $"Исчерпаны попытки ({eng.MaxTransientAttempts}): {t.Reason}", ct);
                        return;
                    }
                    var minutes = eng.BackoffMinutes[Math.Min(run.Attempt - 1, eng.BackoffMinutes.Length - 1)];
                    run.Status = RunStatus.Pending;
                    run.NextAttemptAt = DateTime.UtcNow.AddMinutes(minutes);
                    run.FlushContext();
                    await db.SaveChangesAsync(ct);
                    return;

                case FailResult f:
                    await FailAsync(db, sp, run, f.Reason, ct, f.Notify);
                    return;
            }
        }
    }

    private async Task FailAsync(DocRouterDb db, IServiceProvider sp, DocumentRun run, string reason,
        CancellationToken ct, bool notify = true)
    {
        run.Status = RunStatus.Failed;
        run.Error = reason;
        run.FlushContext();
        log.LogError("Процесс {RunId} ({Type}) остановлен на шаге {Step}: {Reason}", run.Id, run.DocType, run.StepName, reason);

        if (notify)
        {
            await sp.GetRequiredService<INotifier>().EnqueueAsync(run,
                $"[DocRouter] Ошибка обработки: {run.DocType} — {run.Context.FileName}",
                $"""
                Тип документа: {run.DocType}
                Файл: {run.Context.FileName}
                Шаг: {run.StepName}
                Причина: {reason}
                Процесс: {run.Id}
                Время (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}
                """, ct);
        }
        await db.SaveChangesAsync(ct);
    }
}
