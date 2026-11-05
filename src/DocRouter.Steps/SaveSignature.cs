using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Запись подписи в Oracle хранимой процедурой. Процедуре передаётся ID файла (из view; для каталога — имя файла).</summary>
public sealed class SaveSignature(ISignatureStore store) : IStep
{
    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.State.SignatureBase64))
            return StepResult.Fail("Нет подписи для сохранения");

        await store.SaveAsync(new SignatureSaveRequest(
            ctx.Item.Route, ctx.Item.ExternalId, ctx.State.FileName,
            Convert.FromBase64String(ctx.State.SignatureBase64), ctx.Item.Fields), ct);

        return StepResult.Next();
    }
}
