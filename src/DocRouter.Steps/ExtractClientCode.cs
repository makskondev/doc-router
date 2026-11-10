using System.Text.RegularExpressions;
using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>Достаёт код клиента из имени файла (по умолчанию первая группа цифр между подчёркиваниями: *_00000_*).</summary>
public sealed class ExtractClientCode : IStep
{
    public string Pattern { get; set; } = @"_(\d+)_";
    public bool Required { get; set; } = true;

    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        var m = Regex.Match(ctx.State.FileName, Pattern);
        if (m.Success)
        {
            ctx.State.ClientCode = m.Groups[1].Value;
            return Task.FromResult(StepResult.Next());
        }
        return Task.FromResult(Required
            ? StepResult.Fail($"В имени файла нет кода клиента (шаблон {Pattern})")
            : StepResult.Next());
    }
}
