using DocRouter.Core;
using DocRouter.Steps;

namespace DocRouter.Routes;

/// <summary>Единственное место, где разработчик описывает маршруты: тип документа → шаги.</summary>
public static class DocumentRoutes
{
    public static RouteRegistry Build()
    {
        var r = new RoutesBuilder();

        // Общий «хвост»: проверка подписи → запись в БД → файл в результирующую папку
        r.Template("VerifySaveArchive", t => t
            .Step<VerifySignature>()
            .Step<SaveSignature>()
            .Step<MoveFile>(s => s.To = Dir.Result));

        // Сценарии B и C: подпись через REST (B — запись приходит из view, C — файл появился в каталоге; FindFile одинаков)
        r.Template("RestSign", t => t
            .Step<FindFile>()
            .Step<RestSign>()
            .UseTemplate("VerifySaveArchive"));

        // --- Типы документов (примеры — замените на реальные) ---

        // B: запись во view → REST-подпись
        r.For("Invoice").UseTemplate("RestSign");
        r.For("Act").UseTemplate("RestSign");

        // A: запись во view → штамп → папка подписи → ждём файл + .sig → проверка → БД → результат → удалить .sig
        r.For("Agreement")
            .Step<FindFile>()
            .Step<ApplyStamp>(s => s.Stamp = "agreement")        // у каждого типа свой штамп (секция Stamps)
            .Step<WaitForSignatureFile>(s => s.Timeout = TimeSpan.FromHours(24))
            .Step<ReadSignatureFile>()
            .UseTemplate("VerifySaveArchive")
            .Step<DeleteFile>();

        // C: файл появился в каталоге → REST-подпись (источник: Sources:Directories, RequireSignature=false)
        r.For("Letter").UseTemplate("RestSign");

        // D: файл появился уже с подписью (<имя>.sig рядом) (источник: Sources:Directories, RequireSignature=true)
        r.For("SignedLetter")
            .Step<FindFile>()
            .Step<ReadSignatureFile>()
            .UseTemplate("VerifySaveArchive")
            .Step<DeleteFile>();

        return r.Build();
    }
}
