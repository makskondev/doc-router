using Npgsql;

namespace DocRouter.Engine;

/// <summary>Чтобы опрашивал источники только один под. Без пулинга: блокировка живёт ровно пока открыто соединение.</summary>
public static class PgAdvisoryLock
{
    public static async Task<IAsyncDisposable?> TryAcquireAsync(string connectionString, long key, CancellationToken ct)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var conn = new NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn))
        {
            cmd.Parameters.AddWithValue("k", key);
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!)
            {
                await conn.DisposeAsync();
                return null;
            }
        }
        return new Releaser(conn);
    }

    private sealed class Releaser(NpgsqlConnection conn) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => conn.DisposeAsync(); // закрытие сессии снимает блокировку
    }
}
