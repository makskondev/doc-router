using DocRouter.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DocRouter.Host;

/// <summary>Диагностика внешних зависимостей. Не используется как readiness: сбой SMB/Oracle не должен перезапускать под.</summary>
public sealed class DependenciesHealthCheck(
    IFileStore files, IViewReader views, IOptions<RoutesOptions> routes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var problems = new Dictionary<string, object>();

        try { await views.PingAsync(ct); }
        catch (Exception ex) { problems["oracle"] = ex.Message; }

        foreach (var (route, r) in routes.Value)
        {
            foreach (var dir in Enum.GetValues<Dir>())
            {
                var path = r.TryGetDir(dir);
                if (string.IsNullOrEmpty(path)) continue;
                try { await files.ListAsync(path, ct); }
                catch (Exception ex) { problems[$"smb:{route}:{dir}"] = ex.Message; }
            }
        }

        return problems.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Есть проблемы с зависимостями", data: problems);
    }
}
