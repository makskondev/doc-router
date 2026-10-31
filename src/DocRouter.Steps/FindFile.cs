using System.Globalization;
using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Ищет файл документа в папке типа. Не найден → одно письмо (напоминание раз в сутки) и ожидание следующего опроса.</summary>
public sealed class FindFile(IFileStore files, INotifier notifier) : IStep
{
    private const string NotifiedAtKey = "notFoundNotifiedAt";
    public Dir In { get; set; } = Dir.Incoming;

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        var path = Paths.Combine(ctx.Type.GetDir(In), ctx.State.FileName);

        if (await files.ExistsAsync(path, ct))
        {
            ctx.State.CurrentPath = path;
            ctx.State.Extra.Remove(NotifiedAtKey);
            return StepResult.Next();
        }

        var needNotify = !ctx.State.Extra.TryGetValue(NotifiedAtKey, out var raw)
            || DateTime.UtcNow - DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
               > TimeSpan.FromHours(24);

        if (needNotify)
        {
            await notifier.EnqueueAsync(ctx.Run,
                $"[DocRouter] Файл не найден: {ctx.Run.DocType} — {ctx.State.FileName}",
                $"Документ есть в очереди, но файл не найден по пути {path}. Обработка продолжит поиск при каждом опросе.\nПроцесс: {ctx.Run.Id}", ct);
            ctx.State.Extra[NotifiedAtKey] = DateTime.UtcNow.ToString("O");
        }
        return StepResult.Wait(ctx.PollInterval);
    }
}
