namespace DocRouter.Core;

public sealed class EngineOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(10);   // опрос view/каталогов и повторные проверки ожидания
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(30);   // как часто воркер забирает готовые процессы
    public int BatchSize { get; set; } = 20;
    public int MaxParallel { get; set; } = 4;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxTransientAttempts { get; set; } = 5;
    public int[] BackoffMinutes { get; set; } = [1, 5, 15, 60, 240];
    public List<string> DefaultRecipients { get; set; } = new();             // получатели «по умолчанию» (дополнительно к типу)
}

public sealed class DocumentTypeOptions
{
    public string? Incoming { get; set; }
    public string? ToSign { get; set; }
    public string? Signed { get; set; }
    public string? Result { get; set; }
    public List<string> ErrorRecipients { get; set; } = new();

    public string GetDir(Dir dir) => (dir switch
    {
        Dir.Incoming => Incoming,
        Dir.ToSign => ToSign,
        Dir.Signed => Signed,
        Dir.Result => Result,
        _ => null
    }) ?? throw new InvalidOperationException($"Для типа не настроена папка {dir}");
}

/// <summary>Секция "DocumentTypes": ключ = тип документа.</summary>
public sealed class DocumentTypesOptions : Dictionary<string, DocumentTypeOptions> { }

public sealed class SourcesOptions
{
    public List<ViewOptions> Views { get; set; } = new();
}

public sealed class ViewOptions
{
    public string Name { get; set; } = "";             // имя вью в Oracle
    public string IdColumn { get; set; } = "DOC_ID";
    public string FileNameColumn { get; set; } = "FILE_NAME";
    public string? DocTypeColumn { get; set; } = "DOC_TYPE";   // тип из поля вью...
    public string? FixedDocType { get; set; }                  // ...или фиксированный тип для этого вью (приоритетнее)
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
    public string SaveSignatureProcedure { get; set; } = "PKG_DOCS.SAVE_SIGNATURE";
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
