using DocRouter.Core;
using DocRouter.Steps;

namespace DocRouter.Routes;

/// <summary>Единственное место, где разработчик описывает маршруты: тип документа → шаги.</summary>
public static class DocumentRoutes
{
    public static RouteRegistry Build()
    {
        var r = new RoutesBuilder();

        // Сценарий B: запись во вью → файл → REST-подпись → проверка → подпись в БД → файл в результат
        r.Template("RestSignFromView", t => t
            .Step<FindFile>()
            .Step<RestSign>()
            .Step<VerifySignature>()
            .Step<SaveSignature>()
            .Step<MoveFile>(s => s.To = Dir.Result));

        // Типы документов (примеры — замените на реальные 16 типов)
        r.For("Invoice").UseTemplate("RestSignFromView");
        r.For("Act").UseTemplate("RestSignFromView");

        // Сценарии A/C/D добавляются новыми шагами (ApplyStamp, WaitForSignatureFile, ReadSignatureFile, DeleteFile)
        // и новыми маршрутами рядом.

        return r.Build();
    }
}
