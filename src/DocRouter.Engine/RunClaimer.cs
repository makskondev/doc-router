using DocRouter.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DocRouter.Engine;

/// <summary>Захват готовых процессов с арендой (lease): FOR UPDATE SKIP LOCKED. Упавший под → аренда истекает → процесс подхватит другой.</summary>
public sealed class RunClaimer(IServiceScopeFactory scopes, IOptions<EngineOptions> options)
{
    public static readonly string WorkerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    private const string Sql = """
        UPDATE runs SET locked_by = @worker, locked_until = @until
        WHERE id IN (
            SELECT id FROM runs
            WHERE status IN ('Pending','Waiting')
              AND next_attempt_at <= @now
              AND (locked_until IS NULL OR locked_until < @now)
            ORDER BY next_attempt_at
            LIMIT @limit
            FOR UPDATE SKIP LOCKED)
        RETURNING id
        """;

    public async Task<IReadOnlyList<Guid>> ClaimAsync(CancellationToken ct)
    {
        var o = options.Value;
        await using var scope = scopes.CreateAsyncScope();
        var cs = scope.ServiceProvider.GetRequiredService<DocRouterDb>().Database.GetConnectionString();

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        var now = DateTime.UtcNow;
        cmd.Parameters.AddWithValue("worker", WorkerId);
        cmd.Parameters.AddWithValue("until", now + o.LeaseDuration);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("limit", o.BatchSize);

        var ids = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        return ids;
    }
}
