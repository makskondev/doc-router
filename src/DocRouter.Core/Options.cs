namespace DocRouter.Core;

public sealed class CourierOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(10);
    public int MaxParallel { get; set; } = 4;
    public int ReadinessMultiplier { get; set; } = 3;      // «жив», если последний опрос был не позже N интервалов назад
}

/// <summary>
/// Рабочие папки. Каждое свойство = переменная окружения Dirs__&lt;Имя&gt; (пути UNC пишутся как есть, без экранирования).
/// Новая папка = новое свойство здесь + переменная в окружении; свойство без значения — ошибка при старте.
/// </summary>
public sealed class DirectoriesOptions
{
    public string BrokRep { get; set; } = "";          // \\fd\brok_rep
    public string BrokRepEmail { get; set; } = "";     // \\fd\brok_rep\email
    public string SignedDocs { get; set; } = "";       // \\fd\signed_docs
    public string DepoReports { get; set; } = "";      // \\fd\depo_reports
    public string WasSignedDepo { get; set; } = "";    // \\fd\sign_proc\was_signed\depo
    public string WasSignedBrok { get; set; } = "";    // \\fd\sign_proc\was_signed\brok
}

/// <summary>Группы адресатов: Mail__&lt;Группа&gt;=a@x.ru;b@x.ru. Тип документа ссылается на группу в коде: NotifyTo(mail.Brokers).</summary>
public sealed class MailOptions
{
    public string Default { get; set; } = "";          // получают ВСЕ письма об ошибках и системные письма
    public string Brokers { get; set; } = "";
    public string Depo { get; set; } = "";
}

public static class Recipients
{
    public static IReadOnlyList<string> Parse(string? value) =>
        (value ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class SmbOptions
{
    public string Domain { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class SignatureServiceOptions
{
    public string SignUrl { get; set; } = "";
    public string VerifyUrl { get; set; } = "";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

public sealed class OracleOptions
{
    public string ConnectionString { get; set; } = "";
    public string MarkSignedProcedure { get; set; } = "PKG_DOCS.MARK_REPORT_SIGNED";      // семейство 1: подпись + отметка «подписан»
    public string CreateRecordProcedure { get; set; } = "PKG_DOCS.CREATE_SIGNED_FILE";    // семейства 2-4: запись о файле + подпись
}

/// <summary>Собственное хранилище подписей и метаданных (независимо от Oracle).</summary>
public sealed class PostgresOptions
{
    public string ConnectionString { get; set; } = "";
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "docrouter@localhost";
}
