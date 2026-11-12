using Microsoft.Extensions.Options;
using Npgsql;
using DocRouter.Core;

namespace DocRouter.Infrastructure;

/// <summary>Общий пул подключений к PostgreSQL для архива подписей, состояния долгих потоков и журнала операций.</summary>
public sealed class PgDb(IOptions<PostgresOptions> options)
{
    private NpgsqlDataSource? _ds;
    public NpgsqlDataSource Ds => _ds ??= NpgsqlDataSource.Create(options.Value.ConnectionString);
}
