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
    public List<DirectorySourceOptions> Directories { get; set; } = new();
}

/// <summary>Каталог-источник: файл появился в Incoming типа → процесс. Один каталог = один тип.</summary>
public sealed class DirectorySourceOptions
{
    public string DocType { get; set; } = "";
    public bool RequireSignature { get; set; }                  // true: файл приходит сразу с подписью (<имя>.sig рядом)
    public string SignatureSuffix { get; set; } = ".sig";
    public TimeSpan MinAge { get; set; } = TimeSpan.FromMinutes(1);   // файл не меняется не менее этого времени (защита от недокопированных)
    public List<string> Extensions { get; set; } = new();       // пусто = любые; иначе например [".pdf", ".xml"]
}

public sealed class StampLine
{
    public string Text { get; set; } = "";     // с подстановками {ПОЛЕ_ИЗ_VIEW} и {Date}
    public float X { get; set; }
    public float Y { get; set; }               // базовая линия текста, пиксели картинки
    public float FontSize { get; set; } = 22;
}

public sealed class StampOptions
{
    public string? TemplatePath { get; set; }  // PNG-шаблон; нет — прозрачный холст Width×Height
    public int Width { get; set; } = 400;
    public int Height { get; set; } = 150;
    public string FontFamily { get; set; } = "DejaVu Sans";
    public string Color { get; set; } = "#1F3A93";
    public string DateFormat { get; set; } = "dd.MM.yyyy";
    public string? TimeZone { get; set; }      // например "Europe/Moscow"; пусто = локальная зона контейнера
    public List<StampLine> Lines { get; set; } = new();
    // Размещение на PDF (пункты, от левого верхнего угла)
    public int Page { get; set; } = -1;        // <=0 — последняя страница
    public double X { get; set; } = 30;
    public double Y { get; set; } = 30;
    public double WidthPt { get; set; } = 160;
    public double HeightPt { get; set; } = 60;
}

/// <summary>Секция "Stamps": ключ = имя штампа (ссылается ApplyStamp.Stamp).</summary>
public sealed class StampsOptions : Dictionary<string, StampOptions> { }

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
