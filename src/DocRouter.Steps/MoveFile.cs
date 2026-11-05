using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Перемещает файл в папку To. Исходное имя сохраняется; при коллизии имени добавляется суффикс даты-времени.</summary>
public sealed class MoveFile(IFileStore files) : IStep
{
    public string To { get; set; } = "";

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(To)) return StepResult.Fail("MoveFile: не задана папка To");

        var src = ctx.State.CurrentPath!;
        if (!await files.ExistsAsync(src, ct))
            return StepResult.Fail($"Файл {src} не найден при перемещении");

        ctx.State.CurrentPath = await files.MoveUniqueAsync(src, To, ctx.State.FileName, ct);
        ctx.State.FoundIn = To;
        return StepResult.Next();
    }
}
