using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Сохранение подписи в Oracle через хранимую процедуру (идемпотентность — на стороне процедуры).</summary>
public sealed class SaveSignature(ISignatureStore store) : IStep
{
    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.State.SignatureBase64))
            return StepResult.Fail("Нет подписи для сохранения");

        // Для источника-view идентификатор записи лежит в Extra["ExternalId"], для каталога — путь/имя файла
        var externalId = ctx.State.Extra.TryGetValue("ExternalId", out var id) ? id : ctx.Run.SourceKey;

        await store.SaveAsync(new SignatureSaveRequest(
            ctx.Run.DocType, externalId, ctx.State.FileName,
            Convert.FromBase64String(ctx.State.SignatureBase64), ctx.State.Extra), ct);

        return StepResult.Next();
    }
}
