using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Определяет, ГДЕ файл лежит сейчас: ищет файл с исходным именем в папках — либо фиксированных (In), либо выбранных по полям
/// записи (Resolve, например по признаку «отправка по email» и формату отчёта). Папки перечисляются в порядке поиска.
/// Предположение: имена файлов уникальны среди документов, использующих эти папки.
/// </summary>
public sealed class FindFile(IFileStore files) : IStep
{
    public string[] In { get; set; } = [];
    public Func<WorkItem, string[]>? Resolve { get; set; }

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        var folders = Resolve?.Invoke(ctx.Item) ?? In;
        if (folders.Length == 0) return StepResult.Fail("FindFile: не заданы папки поиска (In/Resolve)");

        foreach (var folder in folders)
        {
            var path = Paths.Combine(folder, ctx.State.FileName);
            if (await files.ExistsAsync(path, ct))
            {
                ctx.State.CurrentPath = path;
                ctx.State.FoundIn = folder;
                ctx.State.Content = null;
                return StepResult.Next();
            }
        }
        return StepResult.Fail($"Файл {ctx.State.FileName} не найден в: {string.Join("; ", folders)}");
    }
}
