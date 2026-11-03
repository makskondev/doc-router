namespace DocRouter.Core;

public sealed class CourierOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(10);
    public int MaxParallel { get; set; } = 4;
    public int ReadinessMultiplier { get; set; } = 3;                 // «живой», если последний опрос был не позже N интервалов назад
    public List<string> DefaultRecipients { get; set; } = new();      // получатели системных ошибок и «по умолчанию»
}

/// <summary>Правило определения типа документа по имени файла (общий каталог, разные типы).</summary>
public sealed class TypeRule
{
    public string Type { get; set; } = "";
    public string? Prefix { get; set; }      // CNT_
    public string? Mask { get; set; }        // ADD_*.pdf  (* и ?)
}

/// <summary>Маршрут и его папки. Папки принадлежат маршруту, а не типу документа.</summary>
public sealed class RouteOptions
{
    public string? Incoming { get; set; }
    public string? ToSign { get; set; }
    public string? Signed { get; set; }
    public string? Result { get; set; }
    public List<string> ErrorRecipients { get; set; } = new();
    public List<TypeRule> TypeRules { get; set; } = new();
    public string? DefaultType { get; set; }

    public string? TryGetDir(Dir dir) => dir switch
    {
        Dir.Incoming => Incoming,
        Dir.ToSign => ToSign,
        Dir.Signed => Signed,
        Dir.Result => Result,
        _ => null
    };

    public string GetDir(Dir dir) =>
        TryGetDir(dir) is { Length: > 0 } d ? d : throw new InvalidOperationException($"Для маршрута не настроена папка {dir}");
}

/// <summary>Секция "Routes": ключ = имя маршрута (из DocumentRoutes).</summary>
public sealed class RoutesOptions : Dictionary<string, RouteOptions> { }

/// <summary>Свойства типа документа: штамп и адресаты писем.</summary>
public sealed class DocTypeOptions
{
    public string? Stamp { get; set; }                  // имя секции в Stamps; по умолчанию = имя типа
    public List<string> ErrorRecipients { get; set; } = new();
}

/// <summary>Секция "Types": ключ = тип документа.</summary>
public sealed class TypesOptions : Dictionary<string, DocTypeOptions> { }

public sealed class SourcesOptions
{
    public List<ViewOptions> Views { get; set; } = new();
    public List<DirectorySourceOptions> Directories { get; set; } = new();
}

public sealed class ViewOptions
{
    public string Name { get; set; } = "";               // имя вью в Oracle
    public string Route { get; set; } = "";              // маршрут для записей вью
    public string IdColumn { get; set; } = "DOC_ID";     // ID файла (передаётся процедуре)
    public string FileNameColumn { get; set; } = "FILE_NAME";
    public string? DocTypeColumn { get; set; } = "DOC_TYPE";   // тип из поля вью...
    public string? FixedDocType { get; set; }                  // ...или фиксированный для вью (приоритетнее); иначе TypeRules маршрута
}

/// <summary>Каталог-источник = Incoming маршрута. Тип файла определяется TypeRules маршрута.</summary>
public sealed class DirectorySourceOptions
{
    public string Route { get; set; } = "";
    public bool RequireSignature { get; set; }          // true: файл приходит сразу с подписью (<имя>.sig рядом)
    public string SignatureSuffix { get; set; } = ".sig";
    public TimeSpan MinAge { get; set; } = TimeSpan.FromMinutes(1);   // защита от недокопированных файлов
    public List<string> Extensions { get; set; } = new();             // пусто = любые
}

public sealed class StampLine
{
    public string Text { get; set; } = "";     // с подстановками {ПОЛЕ_ИЗ_VIEW} и {Date}
    public float X { get; set; }
    public float Y { get; set; }
    public float FontSize { get; set; } = 22;
}

public sealed class StampOptions
{
    public string? TemplatePath { get; set; }
    public int Width { get; set; } = 400;
    public int Height { get; set; } = 150;
    public string FontFamily { get; set; } = "DejaVu Sans";
    public string Color { get; set; } = "#1F3A93";
    public string DateFormat { get; set; } = "dd.MM.yyyy";
    public string? TimeZone { get; set; }
    public List<StampLine> Lines { get; set; } = new();
    public int Page { get; set; } = -1;
    public double X { get; set; } = 30;
    public double Y { get; set; } = 30;
    public double WidthPt { get; set; } = 160;
    public double HeightPt { get; set; } = 60;
}

public sealed class StampsOptions : Dictionary<string, StampOptions> { }

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
