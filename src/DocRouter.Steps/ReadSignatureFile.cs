using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Читает файл подписи (<CurrentPath><суффикс>, рядом с файлом) в память. Документы 3 и 4 приходят уже подписанными.</summary>
public sealed class ReadSignatureFile(IFileStore files) : IStep
{
    public string SignatureSuffix { get; set; } = ".sig";

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        var sigPath = ctx.State.CurrentPath + SignatureSuffix;
        if (!await files.ExistsAsync(sigPath, ct)) return StepResult.Fail($"Файл подписи {sigPath} не найден");

        var bytes = await files.ReadAllBytesAsync(sigPath, ct);
        if (bytes.Length == 0) return StepResult.Fail($"Файл подписи {sigPath} пустой");

        ctx.State.SignatureBase64 = Convert.ToBase64String(bytes);
        ctx.State.SignatureSource = "file";
        ctx.State.Extra["sigPath"] = sigPath;
        return StepResult.Next();
    }
}
