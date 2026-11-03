using DocRouter.Core;
using DocRouter.Engine;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DocRouter.Host;

/// <summary>Готов, если последний цикл опроса завершился не позже N интервалов назад (иначе цикл завис).</summary>
public sealed class CourierReadinessCheck(CourierStatus status, TimeProvider time, IOptions<CourierOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var o = options.Value;
        var since = status.LastCycleUtc ?? status.StartedUtc;
        var age = time.GetUtcNow().UtcDateTime - since;
        var limit = o.PollInterval * o.ReadinessMultiplier;

        return Task.FromResult(age <= limit
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"Последний цикл опроса был {age.TotalMinutes:0} мин назад (допустимо {limit.TotalMinutes:0})"));
    }
}
