namespace DocRouter.Core;

public enum SourceKind { View, Directory }
public enum Dir { Incoming, ToSign, Signed, Result }

/// <summary>Документ, найденный источником на текущем опросе. Состояния между опросами нет: «где документ» определяют папки и view.</summary>
public sealed class WorkItem
{
    public required string Route { get; init; }
    public required string DocType { get; init; }        // "" — тип по имени/полю не определён
    public required string FileName { get; init; }
    public required string ExternalId { get; init; }     // ID файла из view; для каталога — имя файла
    public required SourceKind Source { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();   // поля view (данные штампа и т.п.)

    public override string ToString() => $"{Route}/{FileName} ({ExternalId})";
}

/// <summary>Состояние внутри одного прохода по маршруту (в памяти, не сохраняется).</summary>
public sealed class ItemState
{
    public string FileName { get; set; } = "";
    public string? CurrentPath { get; set; }
    public Dir? Location { get; set; }                   // где файл найден на этом проходе (ставит FindFile)
    public string? SignatureBase64 { get; set; }
    public Dictionary<string, string> Extra { get; set; } = new();
}
