namespace DocRouter.Core;

public enum SourceKind { View, Directory }

/// <summary>Документ, найденный источником на текущем опросе. Состояния между опросами нет: «где документ» определяют папки и view.</summary>
public sealed class WorkItem
{
    public required string DocType { get; init; }        // имя типа документа (= его описание в коде)
    public required string FileName { get; init; }
    public required string ExternalId { get; init; }     // ID файла из view; для каталога — имя файла
    public required SourceKind Source { get; init; }
    public IReadOnlyList<string> Recipients { get; init; } = [];   // адресаты ошибок этого типа
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();   // поля view

    public override string ToString() => $"{DocType}/{FileName} ({ExternalId})";
}

/// <summary>Состояние внутри одного прохода по шагам (в памяти, не сохраняется).</summary>
public sealed class ItemState
{
    public string FileName { get; set; } = "";
    public string? CurrentPath { get; set; }
    public string? FoundIn { get; set; }                 // папка, где FindFile нашёл файл на этом проходе
    public string? SignatureBase64 { get; set; }
    public Dictionary<string, string> Extra { get; set; } = new();
}
