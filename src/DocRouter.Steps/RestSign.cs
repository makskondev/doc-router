using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Подпись файла через REST-сервис. Если подпись этого же документа (тот же тип, источник и содержимое) уже лежит в архиве PostgreSQL
/// (прошлый опрос не дошёл до конца), используется она — повторных подписаний и расхождений между архивом и Oracle не бывает.
/// </summary>
public sealed class RestSign(IFileStore files, ISignatureService signer, ISignatureArchive archive) : IStep
{
    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        var bytes = await ctx.ReadContentAsync(files, ct);

        var existing = await archive.FindSignatureAsync(ctx.Item.DocType, ctx.Item.ExternalId, ctx.State.Sha256!, ct);
        if (existing is not null)
        {
            ctx.State.SignatureBase64 = Convert.ToBase64String(existing);
            ctx.State.SignatureSource = "rest";
            return StepResult.Next();
        }

        try
        {
            var sig = await signer.SignAsync(ctx.State.FileName, bytes, ct);
            ctx.State.SignatureBase64 = Convert.ToBase64String(sig);
            ctx.State.SignatureSource = "rest";
            return StepResult.Next();
        }
        catch (SignatureServiceException ex)
        {
            return StepResult.Fail($"REST-подпись: {ex.Message}");
        }
    }
}
