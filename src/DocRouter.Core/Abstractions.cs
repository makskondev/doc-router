using Microsoft.Extensions.Logging;

namespace DocRouter.Core;

// ---------- шаги ----------
public interface IStep
{
    /// <summary>
    /// Шаг выполняется на КАЖДОМ опросе, пока документ не дойдёт до конца маршрута, поэтому он обязан быть
    /// идемпотентным и «знать своё место»: если работа уже сделана раньше (ставит State.Location), вернуть Next().
    /// </summary>
    Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct);
}

public abstract record StepResult
{
    public static StepResult Next() => new NextResult();
    /// <summary>Штатное ожидание (например, подпись ещё не поставлена): без ошибки и письма, вернёмся на следующем опросе.</summary>
    public static StepResult Wait(string reason) => new WaitResult(reason);
    /// <summary>Неудача: письмо уходит на каждую неудачную попытку, повтор — на следующем опросе.</summary>
    public static StepResult Fail(string reason) => new FailResult(reason);
}
public sealed record NextResult : StepResult;
public sealed record WaitResult(string Reason) : StepResult;
public sealed record FailResult(string Reason) : StepResult;

public sealed class StepContext
{
    public required WorkItem Item { get; init; }
    public required ItemState State { get; init; }
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
    /// <summary>Создаёт новый файл строго по указанному пути; если файл уже существует — IOException.</summary>
    Task WriteNewAsync(byte[] content, string path, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
}

public static class Paths
{
    public static string Combine(string dir, string name) => dir.TrimEnd('\\', '/') + "\\" + name;

    /// <summary>Имя файла из UNC-пути (Path.GetFileName на Linux не знает про обратный слэш).</summary>
    public static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    /// <summary>Каталоги сравниваются без учёта регистра, слэшей и хвостового разделителя.</summary>
    public static string NormalizeDir(string path) => path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    public static bool SameDir(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && NormalizeDir(a) == NormalizeDir(b);

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
    string DocType, string ExternalId, string FileName, byte[] Signature, IReadOnlyDictionary<string, string> Fields);

public interface ISignatureStore
{
    /// <summary>Вызов хранимой процедуры с ID файла. Идемпотентность (повторный вызов) обеспечивает сама процедура.</summary>
    Task SaveAsync(SignatureSaveRequest request, CancellationToken ct);
}

// ---------- письма ----------
public interface IMailSender
{
    Task SendAsync(IReadOnlyList<string> to, string subject, string body, CancellationToken ct);
}

public interface INotifier
{
    /// <summary>Письмо о неудаче по документу (адресаты: маршрут + по умолчанию). Без ограничения частоты: на каждую неудачу.</summary>
    Task NotifyFailureAsync(WorkItem item, string step, string reason, CancellationToken ct);
    /// <summary>Системная ошибка (источник недоступен, некорректная запись и т.п.) — адресатам Mail__Default.</summary>
    Task NotifySystemAsync(string subject, string body, CancellationToken ct);
}
