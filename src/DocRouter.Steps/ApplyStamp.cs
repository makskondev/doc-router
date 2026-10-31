using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Сценарий A: накладывает штамп на PDF из текущего места и кладёт проштампованный файл в папку To (по умолчанию ToSign).
/// Исходный файл из входной папки удаляется (т.е. фактически «перемещается»). Только PDF.
/// Повтор после сбоя: если исходника уже нет, а проштампованный файл лежит в To — идём дальше.
/// </summary>
public sealed class ApplyStamp(IFileStore files, IStampRenderer renderer, IPdfStamper stamper) : IStep
{
    public string Stamp { get; set; } = "";
    public Dir To { get; set; } = Dir.ToSign;

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Stamp)) return StepResult.Fail("ApplyStamp: не задано имя штампа (Stamp)");

        var src = ctx.State.CurrentPath!;
        var target = ctx.Type.GetDir(To);

        if (!await files.ExistsAsync(src, ct))
        {
            var done = Paths.Combine(target, ctx.State.FileName);
            if (await files.ExistsAsync(done, ct)) { ctx.State.CurrentPath = done; return StepResult.Next(); }
            return StepResult.Fail($"Файл {src} не найден при наложении штампа");
        }

        var bytes = await files.ReadAllBytesAsync(src, ct);
        if (!IsPdf(bytes)) return StepResult.Fail("Штамп накладывается только на PDF, а файл не является PDF");

        byte[] stamped;
        try
        {
            var image = renderer.Render(Stamp, ctx.State.Extra);
            stamped = stamper.Apply(bytes, image);
        }
        catch (InvalidOperationException ex) { return StepResult.Fail($"Штамп '{Stamp}': {ex.Message}"); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StepResult.Fail($"Не удалось наложить штамп '{Stamp}': {ex.Message}");
        }

        var newPath = await files.WriteUniqueAsync(stamped, target, ctx.State.FileName, ct);
        await files.DeleteAsync(src, ct);
        ctx.State.CurrentPath = newPath;
        return StepResult.Next();
    }

    private static bool IsPdf(byte[] b) =>
        b.Length > 4 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46;   // "%PDF"
}
