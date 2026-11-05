using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Ожидание подписи человеком (сторонним ПО); состояние берём из расположения файла:
///  • файл ещё в ToSign → ждём молча; если лежит дольше Timeout (по времени изменения файла) — Fail;
///  • файл появился в Signed → ждём, пока рядом будет стабильный файл.sig, затем идём дальше.
/// </summary>
public sealed class WaitForSignatureFile(IFileStore files, TimeProvider time) : IStep
{
    public string ToSign { get; set; } = "";
    public string Signed { get; set; } = "";
    public string SignatureSuffix { get; set; } = ".sig";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan StableFor { get; set; } = TimeSpan.FromSeconds(30);

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var name = ctx.State.FileName;

        if (Paths.SameDir(ctx.State.FoundIn, ToSign))
        {
            var entries = await files.ListAsync(ToSign, ct);
            var e = entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (e is not null && now - e.LastWriteUtc > Timeout)
                return StepResult.Fail($"Подпись не получена за {Timeout.TotalHours:0.#} ч: файл {name} лежит в {ToSign} с {e.LastWriteUtc:yyyy-MM-dd HH:mm} UTC");
            return StepResult.Wait("ждём подпись");
        }

        if (Paths.SameDir(ctx.State.FoundIn, Signed))
        {
            var entries = await files.ListAsync(Signed, ct);
            var doc = entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            var sig = entries.FirstOrDefault(x => string.Equals(x.Name, name + SignatureSuffix, StringComparison.OrdinalIgnoreCase));
            if (doc is null || sig is null || now - doc.LastWriteUtc < StableFor || now - sig.LastWriteUtc < StableFor)
                return StepResult.Wait("ждём файл подписи или его стабилизации");

            ctx.State.CurrentPath = doc.FullPath;
            ctx.State.Extra["sigPath"] = sig.FullPath;
            return StepResult.Next();
        }

        return StepResult.Fail($"Файл найден в неожиданной папке при ожидании подписи: {ctx.State.FoundIn}");
    }
}
