using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Семейство 1 (брокерские отчёты): одна процедура Oracle — подпись + отметка «подписан»; запись исчезает из вью. Последний шаг цепочки.</summary>
public sealed class MarkReportSigned(ISignatureStore store) : IStep
{
    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.State.SignatureBase64)) return StepResult.Fail("Нет подписи для сохранения");

        await store.MarkReportSignedAsync(ctx.Item.ExternalId, Convert.FromBase64String(ctx.State.SignatureBase64), ct);
        return StepResult.Next();
    }
}
