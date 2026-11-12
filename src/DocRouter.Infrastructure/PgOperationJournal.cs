using DocRouter.Core;

namespace DocRouter.Infrastructure;

/// <summary>
/// Журнал операций (таблица document_operations): одна строка на документ с последним итогом.
/// Документ, вернувшийся после завершения (или та же запись после «чистки» в Oracle), начинает новый запуск: run_no+1, счётчики с нуля.
/// </summary>
public sealed class PgOperationJournal(PgDb db) : IOperationJournal
{
    public async Task RecordAsync(OperationRecord r, CancellationToken ct)
    {
        var status = r.Outcome.ToString().ToLowerInvariant();   // waiting | failed | completed

        await using var cmd = db.Ds.CreateCommand("""
            INSERT INTO document_operations
                (doc_type, source_key, file_name, kind, client_code, status, last_step, last_message, attempts, completed_at, target_path)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8,
                    CASE WHEN $6 = 'failed' THEN 1 ELSE 0 END,
                    CASE WHEN $6 = 'completed' THEN now() END,
                    $9)
            ON CONFLICT (doc_type, source_key) DO UPDATE SET
                run_no        = CASE WHEN document_operations.status = 'completed' THEN document_operations.run_no + 1 ELSE document_operations.run_no END,
                first_seen_at = CASE WHEN document_operations.status = 'completed' THEN now() ELSE document_operations.first_seen_at END,
                attempts      = CASE WHEN document_operations.status = 'completed' THEN (CASE WHEN EXCLUDED.status = 'failed' THEN 1 ELSE 0 END)
                                     WHEN EXCLUDED.status = 'failed' THEN document_operations.attempts + 1
                                     ELSE document_operations.attempts END,
                file_name     = EXCLUDED.file_name,
                kind          = EXCLUDED.kind,
                client_code   = COALESCE(EXCLUDED.client_code, document_operations.client_code),
                status        = EXCLUDED.status,
                last_step     = EXCLUDED.last_step,
                last_message  = EXCLUDED.last_message,
                completed_at  = CASE WHEN EXCLUDED.status = 'completed' THEN now() END,
                target_path   = COALESCE(EXCLUDED.target_path, CASE WHEN document_operations.status = 'completed' THEN NULL ELSE document_operations.target_path END),
                last_seen_at  = now()
            """);
        cmd.Parameters.AddWithValue(r.DocType);
        cmd.Parameters.AddWithValue(r.SourceKey);
        cmd.Parameters.AddWithValue(r.FileName);
        cmd.Parameters.AddWithValue(r.Kind);
        cmd.Parameters.AddWithValue((object?)r.ClientCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue((object?)r.Step ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)r.Message ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)r.TargetPath ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
