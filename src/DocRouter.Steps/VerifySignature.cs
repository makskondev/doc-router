using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Обязательная проверка подписи. Невалидна → Fail (письмо); исправление — заменить .sig, следующий опрос подхватит.</summary>
public sealed class VerifySignature(IFileStore files, ISignatureService signer) : IStep
{
    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.State.SignatureBase64))
            return StepResult.Fail("Нет подписи для проверки (шаг получения подписи не выполнен)");

        var bytes = await ctx.ReadContentAsync(files, ct);
        try
        {
            var res = await signer.VerifyAsync(ctx.State.FileName, bytes, Convert.FromBase64String(ctx.State.SignatureBase64), ct);
            return res.IsValid ? StepResult.Next() : StepResult.Fail($"Подпись невалидна: {res.Details}");
        }
        catch (SignatureServiceException ex)
        {
            return StepResult.Fail($"Проверка подписи: {ex.Message}");
        }
    }
}
