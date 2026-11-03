using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Сценарий A. Ожидание подписи человеком (сторонним ПО), состояние берём из расположения файла:
///  • файл ещё в ToSign → ждём; если он лежит дольше Timeout (по времени изменения файла) — Fail (письмо на каждом опросе);
///  • файл появился в Signed → ждём, пока рядом будет стабильный файл.sig, затем идём дальше.
/// </summary>
public sealed class WaitForSignatureFile(IFileStore files, TimeProvider time) : IStep
{
    public string SignatureSuffix { get; set; } = ".sig";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan StableFor { get; set; } = TimeSpan.FromSeconds(30);

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var name = ctx.State.FileName;

        switch (ctx.State.Location)
        {
            case Dir.ToSign:
            {
                var entries = await files.ListAsync(ctx.Route.GetDir(Dir.ToSign), ct);
                var e = entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (e is not null && now - e.LastWriteUtc > Timeout)
                    return StepResult.Fail($"Подпись не получена за {Timeout.TotalHours:0.#} ч: файл {name} лежит в папке на подпись с {e.LastWriteUtc:yyyy-MM-dd HH:mm} UTC");
                return StepResult.Wait("ждём подпись");
            }

            case Dir.Signed:
            {
                var entries = await files.ListAsync(ctx.Route.GetDir(Dir.Signed), ct);
                var doc = entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                var sig = entries.FirstOrDefault(x => string.Equals(x.Name, name + SignatureSuffix, StringComparison.OrdinalIgnoreCase));
                if (doc is null || sig is null || now - doc.LastWriteUtc < StableFor || now - sig.LastWriteUtc < StableFor)
                    return StepResult.Wait("ждём файл подписи или его стабилизации");

                ctx.State.CurrentPath = doc.FullPath;
                ctx.State.Extra["sigPath"] = sig.FullPath;
                return StepResult.Next();
            }

            default:
                return StepResult.Fail($"Неожиданное расположение файла при ожидании подписи: {ctx.State.Location}");
        }
    }
}
