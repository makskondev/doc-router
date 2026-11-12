using DocRouter.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DocRouter.Engine;

/// <summary>
/// Проходит шаги типа документа с первого.
/// • Обычный тип (без состояния): Next → следующий шаг, Wait → тихо до следующего опроса, Fail/исключение → в сводку, повтор на следующем опросе.
/// • Тип с .Stateful(): состояние и список выполненных шагов хранятся в PostgreSQL; после каждого успешного шага они сохраняются,
///   а на следующем опросе выполненные шаги пропускаются (шаги всё равно идемпотентны на случай сбоя между действием и сохранением).
/// Журнал операций (все типы) — только наблюдаемость, на ход обработки не влияет.
/// </summary>
public sealed class ItemRunner(
    IServiceProvider services,
    DocumentTypeRegistry types,
    INotifier notifier,
    IStateStore states,
    IOperationJournal journal,
    ILogger<ItemRunner> log)
{
    public async Task RunAsync(WorkItem item, CancellationToken ct)
    {
        using var scope = log.BeginScope(new Dictionary<string, object?>
        {
            ["docType"] = item.DocType, ["file"] = item.FileName, ["id"] = item.ExternalId
        });

        if (!types.TryGet(item.DocType, out var type))
        {
            await FailAsync(item, null, "-", $"Тип документа '{item.DocType}' не описан в коде", ct);
            return;
        }

        var state = new ItemState { FileName = item.FileName };
        var done = new HashSet<string>(StringComparer.Ordinal);

        if (type.Stateful)
        {
            try
            {
                var stored = await states.LoadAsync(item.DocType, item.ExternalId, ct);
                // Завершённое состояние или «все шаги выполнены, а запись всё ещё в очереди» = переобработка: начинаем с чистого листа
                if (stored is { CompletedAtUtc: null } && !type.Steps.All(s => stored.DoneSteps.Contains(s.Name)))
                {
                    state = stored.State;
                    state.FileName = item.FileName;
                    done = new HashSet<string>(stored.DoneSteps, StringComparer.Ordinal);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await FailAsync(item, state, "LoadState", $"Не удалось загрузить состояние из PostgreSQL: {ex.Message}", ct);
                return;
            }
        }

        var ctx = new StepContext { Item = item, State = state, Logger = log };

        foreach (var def in type.Steps)
        {
            ct.ThrowIfCancellationRequested();
            if (type.Stateful && done.Contains(def.Name)) continue;

            var step = (IStep)ActivatorUtilities.CreateInstance(services, def.StepType);
            def.Configure?.Invoke(step);

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
                log.LogError(ex, "Шаг {Step} завершился исключением", def.Name);
                result = StepResult.Fail($"{ex.GetType().Name}: {ex.Message}");
            }

            switch (result)
            {
                case NextResult:
                    if (type.Stateful)
                    {
                        done.Add(def.Name);
                        if (!await TrySaveStateAsync(item, state, done, def.Name, ct)) return;
                    }
                    continue;

                case WaitResult w:
                    log.LogInformation("Шаг {Step}: ожидание ({Reason})", def.Name, w.Reason);
                    if (type.Stateful) await TrySaveStateAsync(item, state, done, def.Name, ct);   // например, момент отправки на подпись
                    await RecordAsync(item, state, OperationOutcome.Waiting, def.Name, w.Reason, ct);
                    return;

                case FailResult f:
                    await FailAsync(item, state, def.Name, f.Reason, ct);
                    return;
            }
        }

        if (type.Stateful)
        {
            try { await states.CompleteAsync(item.DocType, item.ExternalId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // запись из очереди к этому моменту обычно уже ушла: повторить некому, поэтому только лог
                log.LogWarning(ex, "Не удалось отметить завершение состояния");
            }
        }

        log.LogInformation("Документ обработан до конца");
        await RecordAsync(item, state, OperationOutcome.Completed, null, null, ct);
    }

    private async Task<bool> TrySaveStateAsync(WorkItem item, ItemState state, HashSet<string> done, string step, CancellationToken ct)
    {
        try
        {
            await states.SaveAsync(item.DocType, item.ExternalId, state, done.ToList(), ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Действие шага уже выполнено, а запомнить не удалось: на следующем опросе шаг повторится (он идемпотентен)
            await FailAsync(item, state, step, $"Не удалось сохранить состояние в PostgreSQL: {ex.Message}", ct);
            return false;
        }
    }

    private async Task FailAsync(WorkItem item, ItemState? state, string step, string reason, CancellationToken ct)
    {
        log.LogError("Шаг {Step}: {Reason}", step, reason);
        await notifier.NotifyFailureAsync(item, step, reason, ct);
        await RecordAsync(item, state, OperationOutcome.Failed, step, reason, ct);
    }

    private async Task RecordAsync(WorkItem item, ItemState? state, OperationOutcome outcome, string? step, string? message, CancellationToken ct)
    {
        try
        {
            await journal.RecordAsync(new OperationRecord(
                item.DocType, item.ExternalId, item.FileName, item.Kind, state?.ClientCode, outcome, step, message,
                outcome == OperationOutcome.Completed ? state?.CurrentPath : null), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Не удалось записать в журнал операций (на обработку не влияет)");
        }
    }
}
