using DocRouter.Core;

namespace DocRouter.Steps;

/// <summary>
/// Определяет итоговый путь файла в папке To ДО перемещения (путь сохранённого документа нужен Oracle в момент создания записи).
/// Если имя свободно — оно и берётся; при коллизии добавляется суффикс даты-времени ИЗМЕНЕНИЯ исходного файла
/// (детерминированно: повтор на следующем опросе даёт тот же путь), затем счётчик.
/// </summary>
public static class TargetPlanner
{
    public static async Task<string> PlanAsync(IFileStore files, string dir, string fileName, DateTime? sourceModifiedUtc, CancellationToken ct)
    {
        var first = Paths.Combine(dir, fileName);
        if (!await files.ExistsAsync(first, ct)) return first;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var stamp = (sourceModifiedUtc ?? DateTime.UtcNow).ToString("yyyyMMdd_HHmmss");

        for (var i = 0; i < 50; i++)
        {
            var name = i == 0 ? $"{stem}_{stamp}{ext}" : $"{stem}_{stamp}_{i}{ext}";
            var candidate = Paths.Combine(dir, name);
            if (!await files.ExistsAsync(candidate, ct)) return candidate;
        }
        throw new IOException($"Не удалось подобрать свободное имя в {dir} для {fileName}");
    }

    public static DateTime? ModifiedOf(WorkItem item) =>
        DateTime.TryParse(item.Field("SourceModifiedUtc"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
}

public sealed class PlanTargetPath(IFileStore files) : IStep
{
    public string To { get; set; } = "";

    public async Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(To)) return StepResult.Fail("PlanTargetPath: не задана папка To");

        ctx.State.PlannedPath = await TargetPlanner.PlanAsync(files, To, ctx.State.FileName, TargetPlanner.ModifiedOf(ctx.Item), ct);
        return StepResult.Next();
    }
}
