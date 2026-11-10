using DocRouter.Core;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DocRouter.Infrastructure;

/// <summary>
/// Собственный архив подписей и метаданных подписанных файлов (PostgreSQL), независимый от Oracle.
/// Одна таблица; схема создаётся при старте (CREATE TABLE IF NOT EXISTS), миграции не нужны.
/// Ключ документа: (тип, ключ источника — ID записи вью или имя файла, SHA-256 содержимого).
/// </summary>
public sealed class PgSignatureArchive(IOptions<PostgresOptions> options) : ISignatureArchive
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS signed_documents (
            id               bigserial PRIMARY KEY,
            doc_type         text        NOT NULL,
            source_kind      text        NOT NULL,   -- View | Directory
            source_key       text        NOT NULL,   -- ID записи вью или имя файла
            file_name        text        NOT NULL,
            client_code      text,
            kind             text        NOT NULL,   -- «вид»: ДО, жрн, извещение, формат отчёта...
            file_sha256      text        NOT NULL,
            file_size        bigint      NOT NULL,
            source_path      text,
            target_path      text,
            signature        bytea       NOT NULL,
            signature_source text        NOT NULL,   -- rest | file
            signed_at        timestamptz NOT NULL DEFAULT now(),
            updated_at       timestamptz NOT NULL DEFAULT now(),
            UNIQUE (doc_type, source_key, file_sha256)
        );
        CREATE INDEX IF NOT EXISTS ix_signed_documents_file_name ON signed_documents (file_name);
        CREATE INDEX IF NOT EXISTS ix_signed_documents_client ON signed_documents (client_code);
        """;

    private NpgsqlDataSource? _ds;
    private NpgsqlDataSource Ds => _ds ??= NpgsqlDataSource.Create(options.Value.ConnectionString);

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var cmd = Ds.CreateCommand(Schema);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await using var cmd = Ds.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
    }

    public async Task<byte[]?> FindSignatureAsync(string docType, string sourceKey, string sha256, CancellationToken ct)
    {
        await using var cmd = Ds.CreateCommand(
            "SELECT signature FROM signed_documents WHERE doc_type = $1 AND source_key = $2 AND file_sha256 = $3");
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(sourceKey);
        cmd.Parameters.AddWithValue(sha256);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }

    public async Task UpsertAsync(ArchiveRecord r, CancellationToken ct)
    {
        // Первая подпись документа сохраняется; повтор обновляет только метаданные (пути, клиент, вид)
        await using var cmd = Ds.CreateCommand("""
            INSERT INTO signed_documents
                (doc_type, source_kind, source_key, file_name, client_code, kind, file_sha256, file_size,
                 source_path, target_path, signature, signature_source)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            ON CONFLICT (doc_type, source_key, file_sha256) DO UPDATE SET
                file_name   = EXCLUDED.file_name,
                client_code = COALESCE(EXCLUDED.client_code, signed_documents.client_code),
                kind        = EXCLUDED.kind,
                source_path = COALESCE(EXCLUDED.source_path, signed_documents.source_path),
                target_path = COALESCE(EXCLUDED.target_path, signed_documents.target_path),
                updated_at  = now()
            """);
        cmd.Parameters.AddWithValue(r.DocType);
        cmd.Parameters.AddWithValue(r.SourceKind);
        cmd.Parameters.AddWithValue(r.SourceKey);
        cmd.Parameters.AddWithValue(r.FileName);
        cmd.Parameters.AddWithValue((object?)r.ClientCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue(r.Kind);
        cmd.Parameters.AddWithValue(r.Sha256);
        cmd.Parameters.AddWithValue(r.FileSize);
        cmd.Parameters.AddWithValue((object?)r.SourcePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)r.TargetPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue(r.Signature);
        cmd.Parameters.AddWithValue(r.SignatureSource);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
