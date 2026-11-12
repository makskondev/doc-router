using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using DocRouter.Core;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;

namespace DocRouter.Infrastructure;

/// <summary>Чтение очередей-вью из Oracle.</summary>
public sealed partial class OracleViewReader(IOptions<OracleOptions> options) : IViewReader
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_$#]*(\.[A-Za-z][A-Za-z0-9_$#]*)?$")]
    private static partial Regex SafeName();

    public async Task<IReadOnlyList<ViewRecord>> ReadAsync(string viewName, CancellationToken ct)
    {
        if (!SafeName().IsMatch(viewName)) throw new ArgumentException($"Недопустимое имя вью: {viewName}");

        await using var conn = new OracleConnection(options.Value.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {viewName}";

        var rows = new List<ViewRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rd.FieldCount; i++)
                fields[rd.GetName(i)] = rd.IsDBNull(i) ? null : Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture);
            rows.Add(new ViewRecord(fields));
        }
        return rows;
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await using var conn = new OracleConnection(options.Value.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM DUAL";
        await cmd.ExecuteScalarAsync(ct);
    }
}

/// <summary>
/// Запись подписи в Oracle хранимыми процедурами. ПРЕДПОЛОЖЕНИЯ о сигнатурах (поправьте под реальные):
///   MarkSignedProcedure    (P_ID VARCHAR2, P_SIGNATURE BLOB)                                                  — семейство 1
///   CreateRecordProcedure  (P_FILE_NAME, P_KIND, P_CLIENT_CODE VARCHAR2; P_FILE_PATH VARCHAR2; P_SIGNATURE BLOB) — семейства 2-4
/// Идемпотентность повторного вызова обеспечивает сама процедура.
/// </summary>
public sealed class OracleSignatureStore(IOptions<OracleOptions> options) : ISignatureStore
{
    public Task MarkSignedAsync(string recordId, byte[] signature, CancellationToken ct) =>
        CallAsync(options.Value.MarkSignedProcedure, cmd =>
        {
            cmd.Parameters.Add("P_ID", OracleDbType.Varchar2).Value = recordId;
            cmd.Parameters.Add("P_SIGNATURE", OracleDbType.Blob).Value = signature;
        }, ct);

    public Task CreateFileRecordAsync(FileRecordRequest r, CancellationToken ct) =>
        CallAsync(options.Value.CreateRecordProcedure, cmd =>
        {
            cmd.Parameters.Add("P_FILE_NAME", OracleDbType.Varchar2).Value = r.FileName;
            cmd.Parameters.Add("P_KIND", OracleDbType.Varchar2).Value = r.Kind;
            cmd.Parameters.Add("P_CLIENT_CODE", OracleDbType.Varchar2).Value = (object?)r.ClientCode ?? DBNull.Value;
            cmd.Parameters.Add("P_FILE_PATH", OracleDbType.Varchar2).Value = r.SavedPath;
            cmd.Parameters.Add("P_SIGNATURE", OracleDbType.Blob).Value = r.Signature;
        }, ct);

    private async Task CallAsync(string procedure, Action<OracleCommand> bind, CancellationToken ct)
    {
        await using var conn = new OracleConnection(options.Value.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = procedure;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.BindByName = true;
        bind(cmd);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
