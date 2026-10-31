using DocRouter.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DocRouter.Host;

/// <summary>Диагностика внешних зависимостей. Не используется как readiness: сбой SMB/Oracle не должен выводить поды из балансировки.</summary>
public sealed class DependenciesHealthCheck(
    IFileStore files, IViewReader views, IOptions<DocumentTypesOptions> types) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var problems = new Dictionary<string, object>();

        try { await views.PingAsync(ct); }
        catch (Exception ex) { problems["oracle"] = ex.Message; }

        foreach (var (type, t) in types.Value)
        {
            foreach (var (name, dir) in new[] { ("Incoming", t.Incoming), ("Result", t.Result) })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try { await files.ListAsync(dir, ct); }
                catch (Exception ex) { problems[$"smb:{type}:{name}"] = ex.Message; }
            }
        }

        return problems.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Есть проблемы с зависимостями", data: problems);
    }
}
