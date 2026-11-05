using DocRouter.Core;
using DocRouter.Steps;

namespace DocRouter.Routes;

/// <summary>
/// Единственное место, где разработчик описывает типы документов: откуда приходят, кому писать об ошибках, какие шаги проходят.
/// Рабочие папки (dirs) и группы адресатов (mail) приходят из переменных окружения Dirs__* и Mail__* и передаются в шаги значениями.
/// Новый тип = новый блок b.Type(...) ниже (+ при необходимости новая папка в DirectoriesOptions).
/// Шаги выполняются с первого на КАЖДОМ опросе, поэтому FindFile определяет, где файл сейчас, а остальные шаги пропускают сделанное.
/// </summary>
public static class DocumentDefinitions
{
    public static DocumentTypeRegistry Build(DirectoriesOptions dirs, MailOptions mail)
    {
        var b = new DocumentTypesBuilder();

        // БРОКЕРСКИЙ ДОГОВОР: данные из вью, файл лежит в Docs, подпись через REST, после записи подписи файл остаётся в Docs
        b.Type("BROK_AGR")
            .FromView("VW_DOCS")                                   // код в колонке DOC_TYPE = имя типа; иначе: code: "..."
            .NotifyTo(mail.Brokers)
            .Step<FindFile>(s => s.In = [dirs.Docs])
            .Step<RestSign>()
            .Step<VerifySignature>()
            .Step<SaveSignature>();

        // ДЕПОЗИТАРНЫЙ ДОГОВОР: появляется в Signed вместе с <имя>.sig (подпись поставил человек);
        // подпись → БД, файл → Docs, файл подписи удаляем
        b.Type("DEPO_AGR")
            .FromDirectory(dirs.Signed, mask: "DEPO_AGR_*", withSignature: true)
            .NotifyTo(mail.Depo)
            .Step<FindFile>(s => s.In = [dirs.Signed])
            .Step<ReadSignatureFile>()
            .Step<VerifySignature>()
            .Step<SaveSignature>()
            .Step<MoveFile>(s => s.To = dirs.Docs)
            .Step<DeleteFile>();

        // ОТЧЁТЫ: данные из вью, подпись через REST, файл → Reports. Папка-источник и адресаты у типов свои
        void Report(string type, string sourceDir, string recipients) => b.Type(type)
            .FromView("VW_REPORTS")
            .NotifyTo(recipients)
            .Step<FindFile>(s => s.In = [sourceDir])
            .Step<RestSign>()
            .Step<VerifySignature>()
            .Step<SaveSignature>()
            .Step<MoveFile>(s => s.To = dirs.Reports);

        Report("DEPO_REP", dirs.DepoReportsIn, mail.Depo);
        Report("BROK_REP", dirs.BrokReportsIn, mail.Brokers);

        // Пример сценария со штампом (внешняя подпись человеком, ожидание до 24 ч). Папки Incoming/ToSign добавьте в DirectoriesOptions:
        // b.Type("SOME_DOC")
        //     .FromView("VW_DOCS")
        //     .NotifyTo(mail.Brokers)
        //     .Step<FindFile>(s => s.In = [dirs.Signed, dirs.ToSign, dirs.Incoming])      // от продвинутой стадии к начальной
        //     .Step<ApplyStamp>(s => { s.From = dirs.Incoming; s.To = dirs.ToSign; s.Stamp = "some_doc"; })
        //     .Step<WaitForSignatureFile>(s => { s.ToSign = dirs.ToSign; s.Signed = dirs.Signed; })
        //     .Step<ReadSignatureFile>()
        //     .Step<VerifySignature>().Step<SaveSignature>()
        //     .Step<MoveFile>(s => s.To = dirs.Docs)
        //     .Step<DeleteFile>();

        return b.Build();
    }
}
