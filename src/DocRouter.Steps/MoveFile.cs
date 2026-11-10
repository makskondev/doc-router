using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Перемещает файл в путь, определённый PlanTargetPath (тот самый, что ушёл в Oracle). Если PlanTargetPath не было — планирует по папке To.
/// Перезапись исключена: если путь внезапно занят, шаг падает и на следующем опросе путь определяется заново.
/// </summary>
public sealed class MoveFile(IFileStore files) : IStep
{
    public string? To { get; set; }

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        var src = ctx.State.CurrentPath!;
        if (!await files.ExistsAsync(src, ct)) return StepResult.Fail($"Файл {src} не найден при перемещении");

        var dst = ctx.State.PlannedPath;
        if (string.IsNullOrEmpty(dst))
        {
            if (string.IsNullOrEmpty(To)) return StepResult.Fail("MoveFile: нет ни PlannedPath, ни папки To");
            dst = await TargetPlanner.PlanAsync(files, To, ctx.State.FileName, TargetPlanner.ModifiedOf(ctx.Item), ct);
        }

        await files.MoveAsync(src, dst, ct);
        ctx.State.CurrentPath = dst;
        ctx.State.FoundIn = Paths.Directory(dst);
        return StepResult.Next();
    }
}
