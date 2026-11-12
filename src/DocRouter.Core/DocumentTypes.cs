namespace DocRouter.Core;

public sealed record RouteStepDef(string Name, Type StepType, Action<IStep>? Configure);

/// <summary>
/// Записи вью с кодом Code в колонке TypeColumn относятся к этому типу (другие коды игнорируются).
/// TypeColumn = null — все записи вью относятся к этому типу.
/// </summary>
public sealed record ViewSourceDef(string View, string Code, string IdColumn, string NameColumn, string? TypeColumn);

/// <summary>
/// Файлы папки Directory (по маске; с парой .sig, если WithSignature) относятся к этому типу.
/// SignatureTimeout: документ без .sig дольше этого срока — письмо «подпись не получена».
/// </summary>
public sealed record DirectorySourceDef(string Directory, string? Mask, bool WithSignature, string SignatureSuffix,
    TimeSpan MinAge, TimeSpan? SignatureTimeout);

/// <summary>Тип документа: откуда приходит, кому писать об ошибках и какие шаги проходит.</summary>
public sealed class DocumentType
{
    public required string Name { get; init; }
    public required string Kind { get; init; }           // «вид» для Oracle/архива (по умолчанию = имя типа)
    public bool Stateful { get; init; }                  // состояние сохраняется в PostgreSQL (долгие потоки с ожиданием человека)
    public ViewSourceDef? View { get; init; }
    public DirectorySourceDef? Directory { get; init; }
    public required IReadOnlyList<string> Recipients { get; init; }
    public required IReadOnlyList<RouteStepDef> Steps { get; init; }
}

public sealed class DocumentTypeRegistry(IReadOnlyList<DocumentType> types)
{
    private readonly Dictionary<string, DocumentType> _byName =
        types.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DocumentType> All { get; } = types;
    public bool TryGet(string name, out DocumentType type) => _byName.TryGetValue(name, out type!);
}

public sealed class TypeBuilder(string name)
{
    private ViewSourceDef? _view;
    private DirectorySourceDef? _directory;
    private string? _kind;
    private bool _stateful;
    private readonly List<string> _recipients = new();
    private readonly List<RouteStepDef> _steps = new();

    internal string Name { get; } = name;

    /// <summary>
    /// Источник — вью. code — значение колонки типа (по умолчанию = имя типа). Колонки по умолчанию: DOC_ID, FILE_NAME, DOC_TYPE.
    /// typeColumn: null — во вью только этот тип (колонки типа нет).
    /// </summary>
    public TypeBuilder FromView(string view, string? code = null, string idColumn = "DOC_ID",
        string nameColumn = "FILE_NAME", string? typeColumn = "DOC_TYPE")
    {
        EnsureNoSource();
        _view = new ViewSourceDef(view, code ?? Name, idColumn, nameColumn, typeColumn);
        return this;
    }

    /// <summary>Источник — папка. mask: какие файлы папки относятся к типу (обязательна, если папку читают несколько типов).</summary>
    public TypeBuilder FromDirectory(string directory, string? mask = null, bool withSignature = false,
        string signatureSuffix = ".sig", TimeSpan? minAge = null, TimeSpan? signatureTimeout = null)
    {
        EnsureNoSource();
        _directory = new DirectorySourceDef(directory, mask, withSignature, signatureSuffix,
            minAge ?? TimeSpan.FromMinutes(1), signatureTimeout);
        return this;
    }

    /// <summary>
    /// Долгий поток: состояние документа (папка, путь назначения, момент отправки на подпись, выполненные шаги) сохраняется в PostgreSQL
    /// после каждого успешного шага; выполненные шаги на следующих опросах пропускаются. Нужен там, где файл перемещается в середине
    /// цепочки и есть ожидание человека. Остальные типы остаются без состояния.
    /// </summary>
    public TypeBuilder Stateful()
    {
        _stateful = true;
        return this;
    }

    /// <summary>«Вид» документа для Oracle и архива, если он отличается от имени типа (например "ДО").</summary>
    public TypeBuilder Kind(string kind)
    {
        _kind = kind;
        return this;
    }

    /// <summary>Группа адресатов ошибок: NotifyTo(mail.Brokers). Строка вида "a@x.ru;b@x.ru"; вызывать можно несколько раз.</summary>
    public TypeBuilder NotifyTo(string? recipients)
    {
        _recipients.AddRange(Recipients.Parse(recipients));
        return this;
    }

    /// <summary>Шаг; рабочие папки передаются в него значениями: .Step&lt;FindFile&gt;(s => s.In = [dirs.Docs]).</summary>
    public TypeBuilder Step<T>(Action<T>? configure = null) where T : IStep
    {
        var baseName = typeof(T).Name;
        var n = _steps.Count(s => s.Name == baseName || s.Name.StartsWith(baseName + "#"));
        _steps.Add(new RouteStepDef(n == 0 ? baseName : $"{baseName}#{n + 1}", typeof(T),
            configure is null ? null : s => configure((T)s)));
        return this;
    }

    private void EnsureNoSource()
    {
        if (_view is not null || _directory is not null)
            throw new InvalidOperationException($"Тип '{Name}': источник задаётся один раз");
    }

    internal DocumentType Build() => new()
    {
        Name = Name,
        Kind = _kind ?? Name,
        Stateful = _stateful,
        View = _view,
        Directory = _directory,
        Recipients = _recipients.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Steps = _steps.ToList()
    };

    internal bool HasSource => _view is not null || _directory is not null;
    internal int StepCount => _steps.Count;
}

public sealed class DocumentTypesBuilder
{
    private readonly List<TypeBuilder> _types = new();

    public TypeBuilder Type(string name)
    {
        var b = new TypeBuilder(name);
        _types.Add(b);
        return b;
    }

    /// <summary>Собирает реестр и проверяет описание целиком: все ошибки сразу, чтобы сервис падал на старте, а не ночью.</summary>
    public DocumentTypeRegistry Build()
    {
        var errors = new List<string>();

        foreach (var g in _types.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"Тип '{g.Key}' описан несколько раз");

        foreach (var t in _types)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) errors.Add("Тип без имени");
            if (!t.HasSource) errors.Add($"Тип '{t.Name}': не задан источник (FromView/FromDirectory)");
            if (t.StepCount == 0) errors.Add($"Тип '{t.Name}': нет шагов");
        }

        var built = _types.Select(t => t.Build()).ToList();

        foreach (var g in built.Where(t => t.View is not null)
                     .GroupBy(t => (View: t.View!.View.ToUpperInvariant(), Code: t.View!.TypeColumn is null ? "*" : t.View!.Code.ToUpperInvariant()))
                     .Where(g => g.Count() > 1))
            errors.Add($"Вью {g.Key.View}, код '{g.Key.Code}': его заявляют несколько типов ({string.Join(", ", g.Select(t => t.Name))})");

        // Вью без колонки типа целиком принадлежит одному типу: делить его с другими нельзя
        foreach (var g in built.Where(t => t.View is not null).GroupBy(t => t.View!.View.ToUpperInvariant())
                     .Where(g => g.Count() > 1 && g.Any(t => t.View!.TypeColumn is null)))
            errors.Add($"Вью {g.Key}: у типа без колонки типа (typeColumn: null) не может быть соседей ({string.Join(", ", g.Select(t => t.Name))})");

        // Одну папку читают несколько типов: у каждого должна быть своя маска, иначе он заберёт чужие файлы
        foreach (var g in built.Where(t => t.Directory is not null)
                     .GroupBy(t => Paths.NormalizeDir(t.Directory!.Directory)).Where(g => g.Count() > 1))
        {
            var noMask = g.Where(t => string.IsNullOrWhiteSpace(t.Directory!.Mask)).Select(t => t.Name).ToList();
            if (noMask.Count > 0)
                errors.Add($"Папка {g.First().Directory!.Directory} общая для типов {string.Join(", ", g.Select(t => t.Name))}: " +
                           $"у {string.Join(", ", noMask)} нет mask — он заберёт чужие файлы");
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("Ошибки в описании типов документов:\n" + string.Join("\n", errors));

        return new DocumentTypeRegistry(built);
    }
}
