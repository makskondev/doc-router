using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Накладывает штамп на PDF. Работает, только если файл найден в папке From (ещё не проштампован): проштампованный файл
/// пишется в To под тем же именем (без суффикса, чтобы документ находился на следующих опросах), исходный удаляется.
/// Если файл уже в To (или дальше) — штамп поставлен на прошлом опросе: идём дальше и дочищаем исходник, если сбой
/// случился между записью и удалением. Штамп: имя из StampCatalog (по умолчанию = имя типа).
/// </summary>
public sealed class ApplyStamp(IFileStore files, IStampRenderer renderer, IPdfStamper stamper) : IStep
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? Stamp { get; set; }

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(From) || string.IsNullOrEmpty(To))
            return StepResult.Fail("ApplyStamp: не заданы папки From и To");

        if (!Paths.SameDir(ctx.State.FoundIn, From))
        {
            if (Paths.SameDir(ctx.State.FoundIn, To))
            {
                var leftover = Paths.Combine(From, ctx.State.FileName);
                if (await files.ExistsAsync(leftover, ct)) await files.DeleteAsync(leftover, ct);
            }
            return StepResult.Next();
        }

        var stampName = Stamp ?? ctx.Item.DocType;
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

        var dst = Paths.Combine(To, ctx.State.FileName);
        if (await files.ExistsAsync(dst, ct))
            return StepResult.Fail($"В папке {To} уже лежит файл {ctx.State.FileName}; перезаписывать нельзя");

        await files.WriteNewAsync(stamped, dst, ct);
        await files.DeleteAsync(src, ct);

        ctx.State.CurrentPath = dst;
        ctx.State.FoundIn = To;
        return StepResult.Next();
    }

    private static bool IsPdf(byte[] b) =>
        b.Length > 4 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46;   // "%PDF"
}
