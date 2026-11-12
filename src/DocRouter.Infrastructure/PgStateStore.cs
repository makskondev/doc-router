using System.Text.Json;
using DocRouter.Core;
using Npgsql;
using NpgsqlTypes;

namespace DocRouter.Infrastructure;

/// <summary>Состояние документов долгих потоков (таблица document_state). Содержимое файла не хранится.</summary>
public sealed class PgStateStore(PgDb db) : IStateStore
{
    public async Task<StoredState?> LoadAsync(string docType, string sourceKey, CancellationToken ct)
    {
        await using var cmd = db.Ds.CreateCommand(
            "SELECT state::text, done_steps, completed_at FROM document_state WHERE doc_type = $1 AND source_key = $2");
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(sourceKey);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;

        var state = JsonSerializer.Deserialize<ItemState>(rd.GetString(0)) ?? new ItemState();
        var done = rd.GetFieldValue<string[]>(1);
        DateTime? completed = rd.IsDBNull(2) ? null : rd.GetFieldValue<DateTime>(2);
        return new StoredState(state, done, completed);
    }

    public async Task SaveAsync(string docType, string sourceKey, ItemState state, IReadOnlyList<string> doneSteps, CancellationToken ct)
    {
        await using var cmd = db.Ds.CreateCommand("""
            INSERT INTO document_state (doc_type, source_key, state, done_steps)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (doc_type, source_key) DO UPDATE SET
                state = EXCLUDED.state, done_steps = EXCLUDED.done_steps, updated_at = now(), completed_at = NULL
            """);
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(sourceKey);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(state) });
        cmd.Parameters.AddWithValue(doneSteps.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteAsync(string docType, string sourceKey, CancellationToken ct)
    {
        await using var cmd = db.Ds.CreateCommand(
            "UPDATE document_state SET completed_at = now(), updated_at = now() WHERE doc_type = $1 AND source_key = $2");
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(sourceKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
