using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Перекладывает файл из From в папку ручного подписания To (под тем же именем; перезапись исключена) и запоминает момент отправки
/// (Extra["sentToSignAt"]) — от него считается срок ожидания подписи. Повтор после сбоя безопасен: если файл уже лежит в To,
/// шаг только фиксирует момент отправки.
/// </summary>
public sealed class SendToManualSigning(IFileStore files, TimeProvider time) : IStep
{
    public const string SentAtKey = "sentToSignAt";

    public string From { get; set; } = "";
    public string To { get; set; } = "";

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(From) || string.IsNullOrEmpty(To)) return StepResult.Fail("SendToManualSigning: не заданы папки From и To");
        var now = time.GetUtcNow().UtcDateTime;

        if (Paths.SameDir(ctx.State.FoundIn, To))
        {
            if (!ctx.State.Extra.ContainsKey(SentAtKey)) ctx.State.Extra[SentAtKey] = now.ToString("O");   // уже перемещён на прошлом опросе
            return StepResult.Next();
        }

        if (!Paths.SameDir(ctx.State.FoundIn, From))
            return StepResult.Fail($"Файл найден в неожиданной папке: {ctx.State.FoundIn} (ожидалась {From})");

        var target = Paths.Combine(To, ctx.State.FileName);
        if (await files.ExistsAsync(target, ct))
            return StepResult.Fail($"В папке ручного подписания {To} уже лежит файл {ctx.State.FileName}; перезаписывать нельзя");

        await files.MoveAsync(ctx.State.CurrentPath!, target, ct);
        ctx.State.CurrentPath = target;
        ctx.State.FoundIn = To;
        ctx.State.Extra[SentAtKey] = now.ToString("O");
        return StepResult.Next();
    }
}
