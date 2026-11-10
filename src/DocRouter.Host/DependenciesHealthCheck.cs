using DocRouter.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DocRouter.Host;

/// <summary>Диагностика внешних зависимостей. Не используется как readiness: сбой SMB/Oracle не должен перезапускать под.</summary>
public sealed class DependenciesHealthCheck(
    IFileStore files, IViewReader views, ISignatureArchive archive, IOptions<DirectoriesOptions> dirs) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var problems = new Dictionary<string, object>();

        try { await views.PingAsync(ct); }
        catch (Exception ex) { problems["oracle"] = ex.Message; }

        try { await archive.PingAsync(ct); }
        catch (Exception ex) { problems["postgres"] = ex.Message; }

        // Все UNC-папки из Dirs__*
        foreach (var p in typeof(DirectoriesOptions).GetProperties().Where(p => p.PropertyType == typeof(string)))
        {
            var path = (string?)p.GetValue(dirs.Value);
            if (string.IsNullOrEmpty(path) || !path.StartsWith(@"\\")) continue;
            try { await files.ListAsync(path, ct); }
            catch (Exception ex) { problems[$"smb:{p.Name}"] = ex.Message; }
        }

        return problems.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Есть проблемы с зависимостями", data: problems);
    }
}
