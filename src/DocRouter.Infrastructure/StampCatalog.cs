using DocRouter.Core;

namespace DocRouter.Infrastructure;

/// <summary>
/// Описания штампов (живут в коде, а не в конфигурации). Имя штампа указывается в шаге ApplyStamp (Stamp = "...");
/// по умолчанию используется имя типа документа. PNG-шаблоны лежат в папке Dirs__StampTemplates.
/// </summary>
public static class StampCatalog
{
    public static readonly IReadOnlyDictionary<string, StampOptions> All = new Dictionary<string, StampOptions>(StringComparer.OrdinalIgnoreCase)
    {
        // Пример: замените на реальные штампы своих типов
        ["SOME_DOC"] = new StampOptions
        {
            TemplateFile = "some_doc.png",
            TimeZone = "Europe/Moscow",
            Lines =
            [
                new StampLine { Text = "Согласовано {Date}", X = 20, Y = 50, FontSize = 24 },
                new StampLine { Text = "{AUTHOR}", X = 20, Y = 90, FontSize = 20 }
            ],
            Page = -1, X = 30, Y = 30, WidthPt = 160, HeightPt = 60
        }
    };
}
