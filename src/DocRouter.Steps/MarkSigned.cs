using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Процедура Oracle «подпись + отметка по ID» (брокерские отчёты, заявления на ручное подписание): запись исчезает из вью. Ставится ПОСЛЕДНИМ шагом цепочки.</summary>
public sealed class MarkSigned(ISignatureStore store) : IStep
{
    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.State.SignatureBase64)) return StepResult.Fail("Нет подписи для сохранения");

        await store.MarkSignedAsync(ctx.Item.ExternalId, Convert.FromBase64String(ctx.State.SignatureBase64), ct);
        return StepResult.Next();
    }
}
