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
    public string Docs { get; set; } = "";             // \\fileserver\docs
    public string Signed { get; set; } = "";           // \\fileserver\signed
    public string Reports { get; set; } = "";          // \\fileserver\reports
    public string DepoReportsIn { get; set; } = "";    // откуда берутся депозитарные отчёты
    public string BrokReportsIn { get; set; } = "";    // откуда берутся брокерские отчёты
    public string StampTemplates { get; set; } = "";   // локальный путь с PNG-шаблонами штампов (не SMB)
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

public sealed class StampLine
{
    public string Text { get; set; } = "";     // с подстановками {ПОЛЕ_ИЗ_VIEW} и {Date}
    public float X { get; set; }
    public float Y { get; set; }
    public float FontSize { get; set; } = 22;
}

/// <summary>Описание штампа (живёт в коде: StampCatalog).</summary>
public sealed class StampOptions
{
    public string? TemplateFile { get; set; }  // PNG в папке Dirs__StampTemplates; нет — прозрачный холст Width×Height
    public int Width { get; set; } = 400;
    public int Height { get; set; } = 150;
    public string FontFamily { get; set; } = "DejaVu Sans";
    public string Color { get; set; } = "#1F3A93";
    public string DateFormat { get; set; } = "dd.MM.yyyy";
    public string? TimeZone { get; set; }
    public List<StampLine> Lines { get; set; } = new();
    public int Page { get; set; } = -1;        // <=0 — последняя страница
    public double X { get; set; } = 30;
    public double Y { get; set; } = 30;
    public double WidthPt { get; set; } = 160;
    public double HeightPt { get; set; } = 60;
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
