namespace DocRouter.Core;

public enum SourceKind { View, Directory }

/// <summary>Документ, найденный источником на текущем опросе. Состояния между опросами нет: «где документ» определяют папки, view и архив подписей.</summary>
public sealed class WorkItem
{
    public required string DocType { get; init; }        // тип документа (= его описание в коде)
    public required string FileName { get; init; }
    public required string ExternalId { get; init; }     // ID записи из view; для папки — имя файла
    public required SourceKind Source { get; init; }
    public string Kind { get; init; } = "";              // «вид» для Oracle/архива (по умолчанию = имя типа)
    public IReadOnlyList<string> Recipients { get; init; } = [];   // адресаты ошибок этого типа
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();   // поля view / служебные поля папки

    /// <summary>Поле view/папки по имени (регистр не важен).</summary>
    public string? Field(string name) =>
        Fields.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public override string ToString() => $"{DocType}/{FileName} ({ExternalId})";
}

/// <summary>Состояние внутри одного прохода по шагам (в памяти, не сохраняется).</summary>
public sealed class ItemState
{
    public string FileName { get; set; } = "";
    public string? CurrentPath { get; set; }
    public string? FoundIn { get; set; }                 // папка, где FindFile нашёл файл на этом проходе
    public string? PlannedPath { get; set; }             // куда файл будет перемещён (известно ДО перемещения: путь нужен Oracle)
    public string? ClientCode { get; set; }
    public byte[]? Content { get; set; }                 // содержимое файла (читается один раз за проход)
    public string? Sha256 { get; set; }
    public string? SignatureBase64 { get; set; }
    public string? SignatureSource { get; set; }         // "rest" | "file"
    public Dictionary<string, string> Extra { get; set; } = new();
}
