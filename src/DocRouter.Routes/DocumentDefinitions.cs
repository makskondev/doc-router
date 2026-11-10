using DocRouter.Core;
using DocRouter.Steps;

namespace DocRouter.Routes;

/// <summary>
/// Единственное место, где разработчик описывает типы документов: откуда приходят, кому писать об ошибках, какие шаги проходят.
/// Рабочие папки (dirs) и группы адресатов (mail) приходят из переменных окружения Dirs__* и Mail__* и передаются в шаги значениями.
/// Шаги выполняются с первого на КАЖДОМ опросе: FindFile определяет, где файл сейчас, подпись при повторе берётся из архива PostgreSQL,
/// а перемещение — последнее действие (триггером служит сам файл), поэтому повтор после сбоя безопасен.
/// </summary>
public static class DocumentDefinitions
{
    public static DocumentTypeRegistry Build(DirectoriesOptions dirs, MailOptions mail)
    {
        var b = new DocumentTypesBuilder();

        // ===== 1. БРОКЕРСКИЕ ОТЧЁТЫ: данные из вью, файл не перемещается =====
        // Колонки вью (условные): ID, FILE_NAME, CLIENT_ID, REPORT_TYPE (xlsx|csv|xml|pdf), EMAIL (1 — отправка по email)
        b.Type("BROK_REP")
            .FromView("report_log_vw", idColumn: "ID", nameColumn: "FILE_NAME", typeColumn: null)
            .NotifyTo(mail.Brokers)
            .Step<FindFile>(s => s.Resolve = r =>
                string.Equals(r.Field("REPORT_TYPE"), "pdf", StringComparison.OrdinalIgnoreCase) ? [dirs.SignedDocs] :
                r.Field("EMAIL") == "1" ? [dirs.BrokRepEmail] : [dirs.BrokRep])
            .Step<RestSign>()
            .Step<VerifySignature>()
            .Step<ArchiveSignature>(s => { s.ClientField = "CLIENT_ID"; s.KindField = "REPORT_TYPE"; })
            .Step<MarkReportSigned>();                           // подпись + отметка «подписан» одной процедурой

        // ===== 2. ДЕПОЗИТАРНЫЕ ОТЧЁТЫ: появляются в папке, вид — по окончанию имени, код клиента — из имени =====
        void DepoReport(string type, string kind, string mask) => b.Type(type).Kind(kind)
            .FromDirectory(dirs.DepoReports, mask: mask)
            .NotifyTo(mail.Depo)
            .Step<FindFile>(s => s.In = [dirs.DepoReports])
            .Step<ExtractClientCode>()                           // *_00000_*
            .Step<RestSign>()
            .Step<VerifySignature>()
            .Step<PlanTargetPath>(s => s.To = dirs.SignedDocs)   // путь нужен Oracle ДО перемещения
            .Step<ArchiveSignature>()
            .Step<CreateFileRecord>()
            .Step<MoveFile>();

        DepoReport("DEPO_REP_DO", "ДО", "*_ДО.*");
        DepoReport("DEPO_REP_JRN", "жрн", "*_жрн.*");

        // ===== 3 и 4. ДЕПОЗИТАРНЫЕ И БРОКЕРСКИЕ ДОКУМЕНТЫ: появляются с файлом подписи (*.sig), тип — по маске =====
        // Документ без .sig дольше 24 часов → письмо «подпись не получена»
        void SignedDoc(string type, string kind, string sourceDir, string mask, string recipients) => b.Type(type).Kind(kind)
            .FromDirectory(sourceDir, mask: mask, withSignature: true, signatureTimeout: TimeSpan.FromHours(24))
            .NotifyTo(recipients)
            .Step<FindFile>(s => s.In = [sourceDir])
            .Step<ReadSignatureFile>()
            .Step<VerifySignature>()
            .Step<PlanTargetPath>(s => s.To = dirs.SignedDocs)
            .Step<ArchiveSignature>()
            .Step<CreateFileRecord>()
            .Step<MoveFile>()
            .Step<DeleteFile>();                                 // файл подписи (.sig)

        // Маски извещений и заявлений условные — замените на реальные
        SignedDoc("DEPO_NOTICE", "извещение", dirs.WasSignedDepo, "*извещение*", mail.Depo);
        SignedDoc("DEPO_APPLICATION", "заявление", dirs.WasSignedDepo, "*заявление*", mail.Depo);
        SignedDoc("BROK_NOTICE", "извещение", dirs.WasSignedBrok, "*извещение*", mail.Brokers);
        SignedDoc("BROK_APPLICATION", "заявление", dirs.WasSignedBrok, "*заявление*", mail.Brokers);

        return b.Build();
    }
}
