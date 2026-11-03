using DocRouter.Core;
using DocRouter.Steps;

namespace DocRouter.Routes;

/// <summary>
/// Единственное место, где разработчик описывает маршруты: имя маршрута → цепочка шагов.
/// Цепочка выполняется с начала на каждом опросе; шаги сами понимают, что уже сделано (см. FindFile/ApplyStamp/WaitForSignatureFile).
/// Папки, правила определения типа и адресаты — в конфигурации (Routes / Types).
/// </summary>
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

        // Подпись через REST (сценарии B и C)
        r.Template("RestSign", t => t
            .Step<FindFile>()
            .Step<RestSign>()
            .UseTemplate("VerifySaveArchive"));

        // B: запись во view → REST-подпись
        r.For("Invoices").UseTemplate("RestSign");

        // A: запись во view → штамп → папка подписи → ждём файл + .sig → проверка → БД → результат → удалить .sig
        r.For("Agreements")
            .Step<FindFile>()
            .Step<ApplyStamp>()                                   // штамп — по типу документа
            .Step<WaitForSignatureFile>(s => s.Timeout = TimeSpan.FromHours(24))
            .Step<ReadSignatureFile>()
            .UseTemplate("VerifySaveArchive")
            .Step<DeleteFile>();

        // C: файл появился в каталоге → REST-подпись (Sources:Directories, RequireSignature=false)
        r.For("Letters").UseTemplate("RestSign");

        // D: файл появился уже с подписью (<имя>.sig рядом) (Sources:Directories, RequireSignature=true)
        r.For("SignedLetters")
            .Step<FindFile>()
            .Step<ReadSignatureFile>()
            .UseTemplate("VerifySaveArchive")
            .Step<DeleteFile>();

        return r.Build();
    }
}
