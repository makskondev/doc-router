using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Семейства 2-4: запись в Oracle — имя файла, вид, код клиента, путь сохранённого документа, подпись. Нужен PlanTargetPath выше по цепочке.</summary>
public sealed class CreateFileRecord(ISignatureStore store) : IStep
{
    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.State.SignatureBase64)) return StepResult.Fail("Нет подписи для сохранения");
        if (string.IsNullOrEmpty(ctx.State.PlannedPath)) return StepResult.Fail("CreateFileRecord: не определён путь сохранения (нет шага PlanTargetPath)");

        await store.CreateFileRecordAsync(new FileRecordRequest(
            ctx.State.FileName, ctx.Item.Kind, ctx.State.ClientCode, ctx.State.PlannedPath,
            Convert.FromBase64String(ctx.State.SignatureBase64)), ct);

        return StepResult.Next();
    }
}
