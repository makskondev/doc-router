using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Подпись файла через REST-сервис (повторяется на каждом опросе, пока цепочка не дойдёт до конца).</summary>
public sealed class RestSign(IFileStore files, ISignatureService signer) : IStep
{
    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        var bytes = await files.ReadAllBytesAsync(ctx.State.CurrentPath!, ct);
        try
        {
            var sig = await signer.SignAsync(ctx.State.FileName, bytes, ct);
            ctx.State.SignatureBase64 = Convert.ToBase64String(sig);
            return StepResult.Next();
        }
        catch (SignatureServiceException ex)
        {
            return StepResult.Fail($"REST-подпись: {ex.Message}");
        }
    }
}
