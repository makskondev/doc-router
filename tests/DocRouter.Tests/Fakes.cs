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
    public IEnumerable<string> Paths => _files.Keys;

    public Task<IReadOnlyList<FileEntry>> ListAsync(string dir, CancellationToken ct)
    {
        dir = dir.TrimEnd('\\');
        IReadOnlyList<FileEntry> list = _files
            .Where(kv => string.Equals(Core.Paths.Directory(kv.Key), dir, StringComparison.OrdinalIgnoreCase))
            .Select(kv => new FileEntry(Core.Paths.FileName(kv.Key), kv.Key, kv.Value.Data.Length, kv.Value.Mtime))
            .ToList();
        return Task.FromResult(list);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(_files.ContainsKey(path));
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct) => Task.FromResult(_files[path].Data);

    public Task<string> MoveUniqueAsync(string sourcePath, string targetDir, string targetFileName, CancellationToken ct)
    {
        var data = _files[sourcePath];
        var dst = Unique(targetDir, targetFileName);
        _files[dst] = data;
        _files.Remove(sourcePath);
        return Task.FromResult(dst);
    }

    public Task<string> WriteUniqueAsync(byte[] content, string targetDir, string fileName, CancellationToken ct)
    {
        var dst = Unique(targetDir, fileName);
        _files[dst] = (content, DateTime.UtcNow);
        return Task.FromResult(dst);
    }

    public Task DeleteAsync(string path, CancellationToken ct) { _files.Remove(path); return Task.CompletedTask; }

    private string Unique(string dir, string name)
    {
        var p = Core.Paths.Combine(dir, name);
        for (var i = 1; _files.ContainsKey(p); i++)
            p = Core.Paths.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)}_dup{i}{Path.GetExtension(name)}");
        return p;
    }
}

public sealed class CapturingNotifier : INotifier
{
    public List<(string Subject, string Body)> Sent { get; } = new();
    public Task EnqueueAsync(DocumentRun run, string subject, string body, CancellationToken ct = default)
    {
        Sent.Add((subject, body));
        return Task.CompletedTask;
    }
}

public sealed class FakeStampRenderer : IStampRenderer
{
    public StampImage Render(string stampName, IReadOnlyDictionary<string, string> data) =>
        stampName == "missing"
            ? throw new InvalidOperationException("штамп не настроен")
            : new StampImage([1, 2, 3], new StampPlacement(-1, 0, 0, 10, 10));
}

public sealed class FakeStamper : IPdfStamper
{
    public byte[] Apply(byte[] pdf, StampImage stamp) => [.. pdf, .. "STAMPED"u8.ToArray()];
}

public static class Ctx
{
    public const string In = @"\\srv\docs\t\in";
    public const string ToSign = @"\\srv\docs\t\tosign";
    public const string Signed = @"\\srv\docs\t\signed";
    public const string Result = @"\\srv\archive\t";

    public static readonly DocumentTypeOptions Type = new() { Incoming = In, ToSign = ToSign, Signed = Signed, Result = Result };

    public static StepContext Make(string fileName = "a.pdf", string? currentPath = null)
    {
        var run = new DocumentRun { DocType = "T" };
        run.Context.FileName = fileName;
        run.Context.CurrentPath = currentPath;
        return new StepContext
        {
            Run = run,
            State = run.Context,
            Type = Type,
            PollInterval = TimeSpan.FromMinutes(10),
            Logger = NullLogger.Instance
        };
    }
}
