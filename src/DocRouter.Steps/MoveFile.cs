using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Перемещает файл в папку маршрута (To). Исходное имя сохраняется; при коллизии добавляется суффикс даты-времени.</summary>
public sealed class MoveFile(IFileStore files) : IStep
{
    public Dir To { get; set; } = Dir.Result;

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        var src = ctx.State.CurrentPath!;
        if (!await files.ExistsAsync(src, ct))
            return StepResult.Fail($"Файл {src} не найден при перемещении");

        ctx.State.CurrentPath = await files.MoveUniqueAsync(src, ctx.Route.GetDir(To), ctx.State.FileName, ct);
        return StepResult.Next();
    }
}
