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
/// Запись подписи через хранимую процедуру.
/// ПРЕДПОЛОЖЕНИЕ о сигнатуре: (P_DOC_TYPE, P_DOC_ID, P_FILE_NAME, P_SIGNATURE BLOB) — поправьте под реальную.
/// </summary>
public sealed class OracleSignatureStore(IOptions<OracleOptions> options) : ISignatureStore
{
    public async Task SaveAsync(SignatureSaveRequest r, CancellationToken ct)
    {
        await using var conn = new OracleConnection(options.Value.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = options.Value.SaveSignatureProcedure;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.BindByName = true;

        cmd.Parameters.Add("P_DOC_TYPE", OracleDbType.Varchar2).Value = r.DocType;
        cmd.Parameters.Add("P_DOC_ID", OracleDbType.Varchar2).Value = r.ExternalId;
        cmd.Parameters.Add("P_FILE_NAME", OracleDbType.Varchar2).Value = r.FileName;
        cmd.Parameters.Add("P_SIGNATURE", OracleDbType.Blob).Value = r.Signature;

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
