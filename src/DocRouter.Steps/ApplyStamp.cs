using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Сценарий A. Штамп накладывается, только если файл ещё во входной папке; проштампованный файл пишется в ToSign
/// под тем же именем (без суффикса, чтобы документ можно было найти на следующих опросах), исходный удаляется.
/// Если файл уже в ToSign/Signed — штамп поставлен на прошлом опросе: идём дальше (и дочищаем исходник,
/// если сбой случился между записью и удалением). Имя штампа: Stamp → Types:[тип].Stamp → имя типа.
/// </summary>
public sealed class ApplyStamp(IFileStore files, IStampRenderer renderer, IPdfStamper stamper) : IStep
{
    public string? Stamp { get; set; }

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (ctx.State.Location != Dir.Incoming)
        {
            if (ctx.State.Location == Dir.ToSign && ctx.Route.TryGetDir(Dir.Incoming) is { Length: > 0 } incoming)
            {
                var leftover = Paths.Combine(incoming, ctx.State.FileName);
                if (await files.ExistsAsync(leftover, ct)) await files.DeleteAsync(leftover, ct);
            }
            return StepResult.Next();
        }

        var stampName = Stamp ?? ctx.Type.Stamp ?? ctx.Item.DocType;
        if (string.IsNullOrWhiteSpace(stampName))
            return StepResult.Fail("Не определён штамп: тип документа не распознан по имени файла и штамп не задан");

        var src = ctx.State.CurrentPath!;
        var bytes = await files.ReadAllBytesAsync(src, ct);
        if (!IsPdf(bytes)) return StepResult.Fail("Штамп накладывается только на PDF, а файл не является PDF");

        byte[] stamped;
        try
        {
            stamped = stamper.Apply(bytes, renderer.Render(stampName, ctx.Item.Fields));
        }
        catch (InvalidOperationException ex) { return StepResult.Fail($"Штамп '{stampName}': {ex.Message}"); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StepResult.Fail($"Не удалось наложить штамп '{stampName}': {ex.Message}");
        }

        var dst = Paths.Combine(ctx.Route.GetDir(Dir.ToSign), ctx.State.FileName);
        if (await files.ExistsAsync(dst, ct))
            return StepResult.Fail($"В папке на подпись уже лежит файл {ctx.State.FileName}; перезаписывать нельзя");

        await files.WriteNewAsync(stamped, dst, ct);
        await files.DeleteAsync(src, ct);

        ctx.State.CurrentPath = dst;
        ctx.State.Location = Dir.ToSign;
        return StepResult.Next();
    }

    private static bool IsPdf(byte[] b) =>
        b.Length > 4 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46;   // "%PDF"
}
