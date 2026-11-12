using DocRouter.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocRouter.Tests;

public sealed class TestTime(DateTime utcNow) : TimeProvider
{
    public DateTime Now { get; set; } = utcNow;
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
}

/// <summary>IFileStore в памяти: пути вида \\srv\share\dir\file.</summary>
public sealed class InMemoryFileStore : IFileStore
{
    private readonly Dictionary<string, (byte[] Data, DateTime Mtime)> _files = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string path, byte[] data, DateTime? mtime = null) => _files[path] = (data, mtime ?? DateTime.UtcNow.AddHours(-1));
    public bool Has(string path) => _files.ContainsKey(path);
    public byte[] Get(string path) => _files[path].Data;
    public IEnumerable<string> AllPaths => _files.Keys;

    public Task<IReadOnlyList<FileEntry>> ListAsync(string dir, CancellationToken ct)
    {
        dir = dir.TrimEnd('\\');
        IReadOnlyList<FileEntry> list = _files
            .Where(kv => string.Equals(Paths.Directory(kv.Key), dir, StringComparison.OrdinalIgnoreCase))
            .Select(kv => new FileEntry(Paths.FileName(kv.Key), kv.Key, kv.Value.Data.Length, kv.Value.Mtime))
            .ToList();
        return Task.FromResult(list);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(_files.ContainsKey(path));
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct) => Task.FromResult(_files[path].Data);

    public Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct)
    {
        if (_files.ContainsKey(targetPath)) throw new IOException("exists");
        _files[targetPath] = _files[sourcePath];
        _files.Remove(sourcePath);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken ct) { _files.Remove(path); return Task.CompletedTask; }
}

public sealed class CapturingNotifier : INotifier
{
    public List<(string Step, string Reason, WorkItem Item)> Failures { get; } = new();
    public List<string> System { get; } = new();

    public Task NotifyFailureAsync(WorkItem item, string step, string reason, CancellationToken ct)
    {
        Failures.Add((step, reason, item));
        return Task.CompletedTask;
    }

    public Task NotifySystemAsync(string subject, string body, CancellationToken ct)
    {
        System.Add(subject);
        return Task.CompletedTask;
    }

    public int Flushes;
    public Task FlushAsync(CancellationToken ct) { Flushes++; return Task.CompletedTask; }
}

public sealed class CapturingMailSender : IMailSender
{
    public List<(string[] To, string Subject, string Body)> Sent { get; } = new();
    public bool Throw;

    public Task SendAsync(IReadOnlyList<string> to, string subject, string body, CancellationToken ct)
    {
        if (Throw) throw new IOException("smtp down");
        lock (Sent) Sent.Add((to.ToArray(), subject, body));
        return Task.CompletedTask;
    }
}

public sealed class FakeSigner : ISignatureService
{
    public int Signed;
    public bool Valid = true;
    public bool Throw;

    public Task<byte[]> SignAsync(string fileName, byte[] content, CancellationToken ct)
    {
        if (Throw) throw new SignatureServiceException("sign service down", transient: true);
        Signed++;
        return Task.FromResult(new byte[] { 0xAA, (byte)Signed });
    }

    public Task<VerifyResult> VerifyAsync(string fileName, byte[] content, byte[] signature, CancellationToken ct) =>
        Task.FromResult(new VerifyResult(Valid, Valid ? null : "bad"));
}

public sealed class FakeSignatureStore : ISignatureStore
{
    public List<(string Id, byte[] Sig)> Marked { get; } = new();
    public List<FileRecordRequest> Records { get; } = new();

    public Task MarkSignedAsync(string recordId, byte[] signature, CancellationToken ct) { Marked.Add((recordId, signature)); return Task.CompletedTask; }
    public Task CreateFileRecordAsync(FileRecordRequest request, CancellationToken ct) { Records.Add(request); return Task.CompletedTask; }
}

public sealed class InMemoryArchive : ISignatureArchive
{
    public Dictionary<(string, string, string), ArchiveRecord> Rows { get; } = new();

    public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;
    public Task PingAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<byte[]?> FindSignatureAsync(string docType, string sourceKey, string sha256, CancellationToken ct) =>
        Task.FromResult(Rows.TryGetValue((docType, sourceKey, sha256), out var r) ? r.Signature : null);

    public Task UpsertAsync(ArchiveRecord r, CancellationToken ct)
    {
        var key = (r.DocType, r.SourceKey, r.Sha256);
        // как в SQL: первая подпись сохраняется, остальное обновляется
        Rows[key] = Rows.TryGetValue(key, out var old) ? r with { Signature = old.Signature, SignatureSource = old.SignatureSource } : r;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryStateStore : IStateStore
{
    public Dictionary<(string, string), (ItemState State, List<string> Done, DateTime? Completed)> Rows { get; } = new();
    public int Saves;
    public bool ThrowOnSave;
    public bool ThrowOnLoad;

    public Task<StoredState?> LoadAsync(string docType, string sourceKey, CancellationToken ct)
    {
        if (ThrowOnLoad) throw new IOException("pg down");
        // как в БД: храним сериализованную копию, а не ссылку
        return Task.FromResult(Rows.TryGetValue((docType, sourceKey), out var r)
            ? new StoredState(System.Text.Json.JsonSerializer.Deserialize<ItemState>(System.Text.Json.JsonSerializer.Serialize(r.State))!, r.Done.ToList(), r.Completed)
            : null);
    }

    public Task SaveAsync(string docType, string sourceKey, ItemState state, IReadOnlyList<string> doneSteps, CancellationToken ct)
    {
        if (ThrowOnSave) throw new IOException("pg down");
        Saves++;
        Rows[(docType, sourceKey)] = (System.Text.Json.JsonSerializer.Deserialize<ItemState>(System.Text.Json.JsonSerializer.Serialize(state))!, doneSteps.ToList(), null);
        return Task.CompletedTask;
    }

    public Task CompleteAsync(string docType, string sourceKey, CancellationToken ct)
    {
        var r = Rows[(docType, sourceKey)];
        Rows[(docType, sourceKey)] = (r.State, r.Done, DateTime.UtcNow);
        return Task.CompletedTask;
    }
}

public sealed class RecordingJournal : IOperationJournal
{
    public List<OperationRecord> Records { get; } = new();
    public bool Throw;

    public Task RecordAsync(OperationRecord record, CancellationToken ct)
    {
        if (Throw) throw new IOException("pg down");
        Records.Add(record);
        return Task.CompletedTask;
    }
}

public static class Ctx
{
    public const string In = @"\\srv\docs\in";
    public const string ToSign = @"\\srv\docs\tosign";
    public const string Signed = @"\\srv\docs\signed";
    public const string Result = @"\\srv\archive";

    public static WorkItem Item(string docType = "T", string fileName = "a.pdf", string id = "42",
        IReadOnlyDictionary<string, string>? fields = null, string kind = "") => new()
    {
        DocType = docType, FileName = fileName, ExternalId = id, Source = SourceKind.View, Kind = kind,
        Fields = fields ?? new Dictionary<string, string>()
    };

    public static StepContext Make(string fileName = "a.pdf", string? foundIn = null, string? currentPath = null,
        WorkItem? item = null) => new()
    {
        Item = item ?? Item(fileName: fileName),
        State = new ItemState { FileName = fileName, CurrentPath = currentPath, FoundIn = foundIn },
        Logger = NullLogger.Instance
    };
}
