using System.Security.Cryptography;
using DocRouter.Core;

namespace DocRouter.Steps;

public static class StepContextExtensions
{
    /// <summary>Читает файл один раз за проход (RestSign, VerifySignature и ArchiveSignature используют один и тот же буфер) и считает SHA-256.</summary>
    public static async Task<byte[]> ReadContentAsync(this StepContext ctx, IFileStore files, CancellationToken ct)
    {
        if (ctx.State.Content is null)
        {
            ctx.State.Content = await files.ReadAllBytesAsync(ctx.State.CurrentPath!, ct);
            ctx.State.Sha256 = Convert.ToHexString(SHA256.HashData(ctx.State.Content)).ToLowerInvariant();
        }
        return ctx.State.Content;
    }
}
