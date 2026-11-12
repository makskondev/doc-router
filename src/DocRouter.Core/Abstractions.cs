using Microsoft.Extensions.Logging;

namespace DocRouter.Core;

// ---------- шаги ----------
public interface IStep
{
    /// <summary>
    /// Шаг выполняется на КАЖДОМ опросе, пока документ не дойдёт до конца цепочки, поэтому он обязан быть
    /// идемпотентным и «знать своё место» (где лежит файл, есть ли уже подпись в архиве).
    /// </summary>
    Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct);
}

public abstract record StepResult
{
    public static StepResult Next() => new NextResult();
    /// <summary>Штатное ожидание: без ошибки и письма, вернёмся на следующем опросе.</summary>
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
    /// <summary>Перемещает файл строго в targetPath без перезаписи (если файл уже существует — IOException), затем удаляет источник.</summary>
    Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct);
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

// ---------- Oracle ----------
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

/// <summary>Запись о подписанном файле для Oracle (семейства 2-4): имя файла, вид, код клиента, путь сохранённого документа, подпись.</summary>
public sealed record FileRecordRequest(string FileName, string Kind, string? ClientCode, string SavedPath, byte[] Signature);

public interface ISignatureStore
{
    /// <summary>Семейство 1: одна процедура — сохраняет подпись и ставит отметку «подписан» (запись исчезает из view).</summary>
    Task MarkSignedAsync(string recordId, byte[] signature, CancellationToken ct);

    /// <summary>Семейства 2-4: создаёт запись о файле с подписью. Идемпотентность повторного вызова обеспечивает процедура.</summary>
    Task CreateFileRecordAsync(FileRecordRequest request, CancellationToken ct);
}

// ---------- PostgreSQL: независимый архив подписей и метаданных ----------
public sealed record ArchiveRecord(
    string DocType, string SourceKind, string SourceKey, string FileName, string? ClientCode, string Kind,
    string Sha256, long FileSize, string? SourcePath, string? TargetPath, byte[] Signature, string SignatureSource);

public interface ISignatureArchive
{
    Task EnsureSchemaAsync(CancellationToken ct);
    Task PingAsync(CancellationToken ct);

    /// <summary>Ранее сохранённая подпись этого документа (тот же тип, источник и содержимое) — чтобы не подписывать повторно.</summary>
    Task<byte[]?> FindSignatureAsync(string docType, string sourceKey, string sha256, CancellationToken ct);

    /// <summary>Идемпотентная запись: повтор того же документа обновляет строку, а не дублирует.</summary>
    Task UpsertAsync(ArchiveRecord record, CancellationToken ct);
}

// ---------- PostgreSQL: состояние долгих потоков ----------
/// <summary>Сохранённое состояние документа: данные прохода и список уже выполненных шагов. CompletedAtUtc ≠ null — цепочка была завершена.</summary>
public sealed record StoredState(ItemState State, IReadOnlyList<string> DoneSteps, DateTime? CompletedAtUtc);

/// <summary>Хранилище состояния ТОЛЬКО для типов с .Stateful(). Остальные потоки остаются без состояния.</summary>
public interface IStateStore
{
    Task<StoredState?> LoadAsync(string docType, string sourceKey, CancellationToken ct);
    /// <summary>Сохраняет состояние (и снимает отметку «завершён»).</summary>
    Task SaveAsync(string docType, string sourceKey, ItemState state, IReadOnlyList<string> doneSteps, CancellationToken ct);
    Task CompleteAsync(string docType, string sourceKey, CancellationToken ct);
}

// ---------- PostgreSQL: журнал операций (все типы) ----------
public enum OperationOutcome { Waiting, Failed, Completed }

/// <summary>Итог одного прохода по документу: ждёт, упал на шаге или завершён.</summary>
public sealed record OperationRecord(
    string DocType, string SourceKey, string FileName, string Kind, string? ClientCode,
    OperationOutcome Outcome, string? Step, string? Message, string? TargetPath);

/// <summary>
/// Журнал операций: когда документ впервые замечен, где остановился, сколько неудачных попыток, чем закончился. Только для наблюдаемости:
/// на ход обработки не влияет, сбой записи в журнал логируется и обработку не ломает.
/// </summary>
public interface IOperationJournal
{
    Task RecordAsync(OperationRecord record, CancellationToken ct);
}

// ---------- письма ----------
public interface IMailSender
{
    Task SendAsync(IReadOnlyList<string> to, string subject, string body, CancellationToken ct);
}

/// <summary>
/// Уведомления об ошибках собираются в течение опроса и уходят одной сводкой: FlushAsync в конце опроса отправляет
/// по одному письму на набор получателей — со всеми необработанными документами и причиной по каждому.
/// </summary>
public interface INotifier
{
    /// <summary>Необработанный документ (получатели: группа типа из NotifyTo + Mail__Default). Только накапливает — письмо уйдёт в FlushAsync.</summary>
    Task NotifyFailureAsync(WorkItem item, string step, string reason, CancellationToken ct);
    /// <summary>Системная ошибка (источник недоступен, некорректная запись и т.п.) — попадёт в сводку получателям Mail__Default.</summary>
    Task NotifySystemAsync(string subject, string body, CancellationToken ct);
    /// <summary>Отправляет накопленное за опрос (если оно есть) и очищает накопитель.</summary>
    Task FlushAsync(CancellationToken ct);
}
