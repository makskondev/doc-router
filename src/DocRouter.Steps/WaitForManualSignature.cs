using System.Globalization;
using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Ждёт, пока ПО ручного подписания положит в папку Signed подписанный файл вместе с файлом.sig (оба не менялись StableFor).
/// Срок ожидания считается от момента отправки на подпись (SendToManualSigning); после Timeout — Fail (строка в сводке на каждом опросе).
/// Пока не появилось — Wait (без ошибки). Шаг не зависит от FoundIn: подписанный файл всегда ищется в Signed.
/// </summary>
public sealed class WaitForManualSignature(IFileStore files, TimeProvider time) : IStep
{
    public string Signed { get; set; } = "";
    public string SignatureSuffix { get; set; } = ".sig";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan StableFor { get; set; } = TimeSpan.FromSeconds(30);

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Signed)) return StepResult.Fail("WaitForManualSignature: не задана папка Signed");

        var now = time.GetUtcNow().UtcDateTime;
        var name = ctx.State.FileName;

        var entries = await files.ListAsync(Signed, ct);
        var doc = entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        var sig = entries.FirstOrDefault(x => string.Equals(x.Name, name + SignatureSuffix, StringComparison.OrdinalIgnoreCase));

        if (doc is not null && sig is not null && now - doc.LastWriteUtc >= StableFor && now - sig.LastWriteUtc >= StableFor)
        {
            ctx.State.CurrentPath = doc.FullPath;
            ctx.State.FoundIn = Signed;
            ctx.State.Content = null;
            ctx.State.Extra["sigPath"] = sig.FullPath;
            return StepResult.Next();
        }

        if (ctx.State.Extra.TryGetValue(SendToManualSigning.SentAtKey, out var raw))
        {
            var sentAt = DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (now - sentAt > Timeout)
                return StepResult.Fail($"Подпись не получена за {Timeout.TotalHours:0.#} ч: файл отправлен на ручное подписание {sentAt:yyyy-MM-dd HH:mm} UTC, подписанного файла и {name}{SignatureSuffix} в {Signed} нет");
        }
        else
        {
            ctx.State.Extra[SendToManualSigning.SentAtKey] = now.ToString("O");     // страховка: начинаем отсчёт, если момент отправки неизвестен
        }

        return StepResult.Wait("ждём подписанный файл и файл подписи");
    }
}
