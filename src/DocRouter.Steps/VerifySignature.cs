using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Обязательная проверка подписи (REST API проверки). Невалидна → стоп + письмо.</summary>
public sealed class VerifySignature(IFileStore files, ISignatureService signer) : IStep
{
    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.State.SignatureBase64))
            return StepResult.Fail("Нет подписи для проверки (шаг получения подписи не выполнен)");

        var bytes = await files.ReadAllBytesAsync(ctx.State.CurrentPath!, ct);
        try
        {
            var res = await signer.VerifyAsync(ctx.State.FileName, bytes, Convert.FromBase64String(ctx.State.SignatureBase64), ct);
            return res.IsValid ? StepResult.Next() : StepResult.Fail($"Подпись невалидна: {res.Details}");
        }
        catch (SignatureServiceException ex)
        {
            return ex.Transient ? StepResult.Transient(ex.Message) : StepResult.Fail($"Проверка подписи: {ex.Message}");
        }
    }
}
