using Microsoft.Extensions.Logging;

namespace DocRouter.Core;

// ---------- шаги ----------
public interface IStep
{
    /// <summary>Шаг ДОЛЖЕН быть безопасен для повторного выполнения.</summary>
    Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct);
}

public abstract record StepResult
{
    public static StepResult Next() => new NextResult();
    public static StepResult Wait(TimeSpan delay) => new WaitResult(delay);
    public static StepResult Transient(string reason) => new TransientResult(reason);
    public static StepResult Fail(string reason, bool notify = true) => new FailResult(reason, notify);
}
public sealed record NextResult : StepResult;
public sealed record WaitResult(TimeSpan Delay) : StepResult;
public sealed record TransientResult(string Reason) : StepResult;
public sealed record FailResult(string Reason, bool Notify) : StepResult;

public sealed class StepContext
{
    public required DocumentRun Run { get; init; }
    public required RunContextData State { get; init; }
    public required DocumentTypeOptions Type { get; init; }
    public required TimeSpan PollInterval { get; init; }
    public required ILogger Logger { get; init; }
}

// ---------- файлы (SMB) ----------
public sealed record FileEntry(string Name, string FullPath, long Size, DateTime LastWriteUtc);

public interface IFileStore
{
    Task<IReadOnlyList<FileEntry>> ListAsync(string dir, CancellationToken ct);
    Task<bool> ExistsAsync(string path, CancellationToken ct);
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct);
    /// <summary>Копирует в targetDir без перезаписи (при коллизии имени добавляет суффикс даты-времени), затем удаляет источник. Возвращает итоговый путь.</summary>
    Task<string> MoveUniqueAsync(string sourcePath, string targetDir, string targetFileName, CancellationToken ct);
    /// <summary>Создаёт новый файл в targetDir без перезаписи (при коллизии имени добавляет суффикс). Возвращает итоговый путь.</summary>
    Task<string> WriteUniqueAsync(byte[] content, string targetDir, string fileName, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
}

public static class Paths
{
    public static string Combine(string dir, string name) => dir.TrimEnd('\\', '/') + "\\" + name;

    /// <summary>Имя файла из UNC-пути (Path.GetFileName на Linux не знает про обратный слэш).</summary>
    public static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    public static string Directory(string path)
    {
        var i = path.LastIndexOfAny(['\\', '/']);
        return i < 0 ? "" : path[..i];
    }
}

// ---------- штамп ----------
/// <summary>Координаты в пунктах PDF от левого верхнего угла. Page &lt;= 0 — последняя страница.</summary>
public sealed record StampPlacement(int Page, double X, double Y, double Width, double Height);
public sealed record StampImage(byte[] Png, StampPlacement Placement);

public interface IStampRenderer
{
    /// <summary>Рисует картинку штампа по шаблону с именем stampName. Данные — поля из контекста процесса.</summary>
    StampImage Render(string stampName, IReadOnlyDictionary<string, string> data);
}

public interface IPdfStamper
{
    byte[] Apply(byte[] pdf, StampImage stamp);
}

// ---------- REST-подпись ----------
public sealed record VerifyResult(bool IsValid, string? Details);

public sealed class SignatureServiceException(string message, bool transient) : Exception(message)
{
    public bool Transient { get; } = transient;
}

public interface ISignatureService
{
    Task<byte[]> SignAsync(string fileName, byte[] content, CancellationToken ct);
    Task<VerifyResult> VerifyAsync(string fileName, byte[] content, byte[] signature, CancellationToken ct);
}

// ---------- сторонняя БД (Oracle) ----------
public sealed record ViewRecord(IReadOnlyDictionary<string, string?> Fields)
{
    public string? Get(string column) =>
        Fields.TryGetValue(column, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}

public interface IViewReader
{
    Task<IReadOnlyList<ViewRecord>> ReadAsync(string viewName, CancellationToken ct);
    Task PingAsync(CancellationToken ct);
}

public sealed record SignatureSaveRequest(
    string DocType, string ExternalId, string FileName, byte[] Signature, IReadOnlyDictionary<string, string> Extra);

public interface ISignatureStore
{
    /// <summary>Вызов хранимой процедуры. Идемпотентность (повторный вызов) обеспечивает сама процедура.</summary>
    Task SaveAsync(SignatureSaveRequest request, CancellationToken ct);
}

// ---------- письма ----------
public interface IMailSender
{
    Task SendAsync(IReadOnlyList<string> to, string subject, string body, CancellationToken ct);
}

public interface INotifier
{
    /// <summary>Кладёт письмо в outbox в рамках текущего юнита работы (сохраняется вместе с состоянием процесса).</summary>
    Task EnqueueAsync(DocumentRun run, string subject, string body, CancellationToken ct = default);
}
