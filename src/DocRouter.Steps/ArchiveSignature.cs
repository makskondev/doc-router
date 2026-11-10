using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Сохраняет подпись и метаданные файла в собственный архив PostgreSQL (независимо от Oracle). Идёт сразу после проверки подписи,
/// ДО записи в Oracle и перемещения: собственная копия подписи появляется первой. Идемпотентно (повтор обновляет ту же строку).
/// ClientField / KindField — имена колонок вью, откуда брать код клиента и «вид» (для документов из папок они берутся из имени/типа).
/// </summary>
public sealed class ArchiveSignature(IFileStore files, ISignatureArchive archive) : IStep
{
    public string? ClientField { get; set; }
    public string? KindField { get; set; }

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.State.SignatureBase64)) return StepResult.Fail("Нет подписи для архива");

        var bytes = await ctx.ReadContentAsync(files, ct);
        var client = ctx.State.ClientCode ?? (ClientField is null ? null : ctx.Item.Field(ClientField));
        var kind = (KindField is null ? null : ctx.Item.Field(KindField)) ?? ctx.Item.Kind;
        if (client is not null) ctx.State.ClientCode = client;

        await archive.UpsertAsync(new ArchiveRecord(
            ctx.Item.DocType, ctx.Item.Source.ToString(), ctx.Item.ExternalId, ctx.State.FileName, client, kind,
            ctx.State.Sha256!, bytes.LongLength, ctx.State.CurrentPath, ctx.State.PlannedPath,
            Convert.FromBase64String(ctx.State.SignatureBase64), ctx.State.SignatureSource ?? "rest"), ct);

        return StepResult.Next();
    }
}
