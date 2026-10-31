using System.Text.Json;

namespace DocRouter.Core;

public enum RunStatus { Pending, Waiting, Failed, Completed, Cancelled }
public enum SourceKind { View, Directory }
public enum Dir { Incoming, ToSign, Signed, Result }
public enum NotificationStatus { Pending, Sent, Failed }

/// <summary>Контекст процесса (хранится в jsonb). Шаги обмениваются данными только через него.</summary>
public sealed class RunContextData
{
    public string FileName { get; set; } = "";          // исходное имя файла (сохраняем как есть)
    public string? CurrentPath { get; set; }            // где файл лежит сейчас (UNC)
    public string? SignatureBase64 { get; set; }
    public Dictionary<string, string> Extra { get; set; } = new();   // поля из view (данные штампа и т.п.), служебные флаги
}

/// <summary>Один документ на одном маршруте.</summary>
public sealed class DocumentRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DocType { get; set; } = "";
    public SourceKind Source { get; set; }
    public string SourceKey { get; set; } = "";
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public int StepIndex { get; set; }
    public string StepName { get; set; } = "";
    public int Attempt { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public string? LockedBy { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string ContextJson { get; set; } = "{}";
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    private RunContextData? _ctx;
    /// <summary>Не маппится в EF (см. DocRouterDb). Перед SaveChanges вызывать FlushContext().</summary>
    public RunContextData Context => _ctx ??= JsonSerializer.Deserialize<RunContextData>(ContextJson) ?? new();
    public void FlushContext() { if (_ctx is not null) ContextJson = JsonSerializer.Serialize(_ctx); }
}

public sealed class RunStepLog
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public int StepIndex { get; set; }
    public string StepName { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public string Outcome { get; set; } = "";
    public string? Error { get; set; }
}

/// <summary>Outbox писем: пишется в одной транзакции с состоянием процесса.</summary>
public sealed class Notification
{
    public long Id { get; set; }
    public Guid? RunId { get; set; }
    public string Recipients { get; set; } = "";   // через ';'
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
}
