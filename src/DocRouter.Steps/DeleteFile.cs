using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Удаляет файл, путь которого лежит в Extra[PathKey] (по умолчанию — файл подписи). Идемпотентен.</summary>
public sealed class DeleteFile(IFileStore files) : IStep
{
    public string PathKey { get; set; } = "sigPath";

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (ctx.State.Extra.TryGetValue(PathKey, out var path))
        {
            if (await files.ExistsAsync(path, ct)) await files.DeleteAsync(path, ct);
            ctx.State.Extra.Remove(PathKey);
        }
        return StepResult.Next();
    }
}
