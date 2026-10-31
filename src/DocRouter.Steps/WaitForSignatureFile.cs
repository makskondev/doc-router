using System.Globalization;
using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Сценарий A: ждёт в папке подписанных (In) пару «файл + файл.sig» (подпись делает человек сторонним ПО).
/// Пара считается готовой, когда оба файла не менялись StableFor. Таймаут (24 ч) → Fail + письмо.
/// </summary>
public sealed class WaitForSignatureFile(IFileStore files, TimeProvider time) : IStep
{
    private const string StartedKey = "waitSignatureStartedAt";

    public Dir In { get; set; } = Dir.Signed;
    public string SignatureSuffix { get; set; } = ".sig";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan StableFor { get; set; } = TimeSpan.FromSeconds(30);

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        DateTime started;
        if (ctx.State.Extra.TryGetValue(StartedKey, out var raw))
            started = DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        else
        {
            started = now;
            ctx.State.Extra[StartedKey] = now.ToString("O");
        }

        var name = Paths.FileName(ctx.State.CurrentPath!);
        var entries = await files.ListAsync(ctx.Type.GetDir(In), ct);
        var doc = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        var sig = entries.FirstOrDefault(e => string.Equals(e.Name, name + SignatureSuffix, StringComparison.OrdinalIgnoreCase));

        if (doc is not null && sig is not null && now - doc.LastWriteUtc >= StableFor && now - sig.LastWriteUtc >= StableFor)
        {
            ctx.State.CurrentPath = doc.FullPath;
            ctx.State.Extra["sigPath"] = sig.FullPath;
            ctx.State.Extra.Remove(StartedKey);
            return StepResult.Next();
        }

        if (now - started > Timeout)
            return StepResult.Fail($"Подписанный файл и подпись ({name} + {name}{SignatureSuffix}) не появились в {ctx.Type.GetDir(In)} за {Timeout.TotalHours:0.#} ч");

        return StepResult.Wait(ctx.PollInterval);
    }
}
