using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Определяет, ГДЕ файл лежит сейчас: ищет по имени в папках маршрута, начиная с самой «продвинутой»
/// (Signed → ToSign → Incoming). Расположение — это и есть состояние документа между опросами.
/// Result намеренно не ищем: после перемещения в результат документ завершён.
/// Предположение: имена файлов уникальны среди документов маршрута, находящихся в работе.
/// </summary>
public sealed class FindFile(IFileStore files) : IStep
{
    private static readonly Dir[] SearchOrder = [Dir.Signed, Dir.ToSign, Dir.Incoming];

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        foreach (var dir in SearchOrder)
        {
            var folder = ctx.Route.TryGetDir(dir);
            if (string.IsNullOrEmpty(folder)) continue;

            var path = Paths.Combine(folder, ctx.State.FileName);
            if (await files.ExistsAsync(path, ct))
            {
                ctx.State.CurrentPath = path;
                ctx.State.Location = dir;
                return StepResult.Next();
            }
        }
        return StepResult.Fail($"Файл {ctx.State.FileName} не найден в папках маршрута {ctx.Item.Route}");
    }
}
