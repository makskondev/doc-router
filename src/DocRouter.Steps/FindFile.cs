using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Определяет, ГДЕ файл лежит сейчас: ищет файл с исходным именем в папках In в указанном порядке —
/// от самой «продвинутой» стадии к начальной (например [signed, toSign, incoming]). Найденная папка запоминается (FoundIn)
/// и служит состоянием документа между опросами. Предположение: имена уникальны среди документов, использующих эти папки.
/// </summary>
public sealed class FindFile(IFileStore files) : IStep
{
    public string[] In { get; set; } = [];

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (In.Length == 0) return StepResult.Fail("FindFile: не заданы папки поиска (In)");

        foreach (var folder in In)
        {
            var path = Paths.Combine(folder, ctx.State.FileName);
            if (await files.ExistsAsync(path, ct))
            {
                ctx.State.CurrentPath = path;
                ctx.State.FoundIn = folder;
                return StepResult.Next();
            }
        }
        return StepResult.Fail($"Файл {ctx.State.FileName} не найден в: {string.Join("; ", In)}");
    }
}
