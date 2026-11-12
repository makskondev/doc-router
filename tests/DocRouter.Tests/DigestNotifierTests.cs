using DocRouter.Core;
using DocRouter.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocRouter.Tests;

public class DigestNotifierTests
{
    private static (DigestNotifier n, CapturingMailSender mail) Create(string defaults = "all@x.ru")
    {
        var mail = new CapturingMailSender();
        var n = new DigestNotifier(mail, Options.Create(new MailOptions { Default = defaults }),
            new TestTime(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc)), NullLogger<DigestNotifier>.Instance);
        return (n, mail);
    }

    private static WorkItem Doc(string type, string file, string id, params string[] recipients) => new()
    {
        DocType = type, FileName = file, ExternalId = id, Source = SourceKind.Directory, Recipients = recipients
    };

    [Fact]
    public async Task All_failures_of_one_poll_go_into_one_email_with_reason_per_file()
    {
        var (n, mail) = Create(defaults: "");
        await n.NotifyFailureAsync(Doc("DEPO_NOTICE", "a.pdf", "1", "d@x.ru"), "VerifySignature", "Подпись невалидна", default);
        await n.NotifyFailureAsync(Doc("DEPO_NOTICE", "b.pdf", "2", "d@x.ru"), "FindFile", "Файл не найден", default);
        await n.NotifyFailureAsync(Doc("DEPO_REP_DO", "c_00001_ДО.pdf", "3", "d@x.ru"), "RestSign", "REST-подпись: таймаут", default);
        await n.FlushAsync(default);

        var m = Assert.Single(mail.Sent);                           // ОДНО письмо, а не три
        Assert.Equal(new[] { "d@x.ru" }, m.To);
        Assert.Contains("Необработанных документов: 3", m.Subject);
        foreach (var expected in new[] { "a.pdf", "b.pdf", "c_00001_ДО.pdf", "Подпись невалидна", "Файл не найден", "таймаут", "VerifySignature", "FindFile", "RestSign", "DEPO_NOTICE (2)", "DEPO_REP_DO (1)", "ID 2" })
            Assert.Contains(expected, m.Body);
    }

    [Fact]
    public async Task Default_recipients_get_everything_and_each_group_only_its_own_documents()
    {
        var (n, mail) = Create(defaults: "all@x.ru");
        await n.NotifyFailureAsync(Doc("BROK_NOTICE", "brok.pdf", "1", "brok@x.ru"), "S", "r1", default);
        await n.NotifyFailureAsync(Doc("DEPO_NOTICE", "depo.pdf", "2", "depo@x.ru"), "S", "r2", default);
        await n.FlushAsync(default);

        Assert.Equal(3, mail.Sent.Count);
        var all = mail.Sent.Single(s => s.To.Contains("all@x.ru"));
        Assert.Contains("brok.pdf", all.Body);
        Assert.Contains("depo.pdf", all.Body);

        var brok = mail.Sent.Single(s => s.To.Contains("brok@x.ru"));
        Assert.Contains("brok.pdf", brok.Body);
        Assert.DoesNotContain("depo.pdf", brok.Body);

        var depo = mail.Sent.Single(s => s.To.Contains("depo@x.ru"));
        Assert.Contains("depo.pdf", depo.Body);
        Assert.DoesNotContain("brok.pdf", depo.Body);
    }

    [Fact]
    public async Task Recipients_with_identical_sets_share_one_email_and_a_default_who_is_also_in_a_group_gets_one_email()
    {
        var (n, mail) = Create(defaults: "boss@x.ru");
        await n.NotifyFailureAsync(Doc("BROK_NOTICE", "a.pdf", "1", "b1@x.ru", "b2@x.ru", "boss@x.ru"), "S", "r", default);
        await n.FlushAsync(default);

        Assert.Equal(1, mail.Sent.Count);
        Assert.Equal(new[] { "b1@x.ru", "b2@x.ru", "boss@x.ru" }, mail.Sent[0].To.OrderBy(x => x));
    }

    [Fact]
    public async Task System_errors_go_to_default_only_in_their_own_section()
    {
        var (n, mail) = Create(defaults: "all@x.ru");
        await n.NotifySystemAsync("Вью X недоступно", "ORA-00942", default);
        await n.NotifyFailureAsync(Doc("BROK_NOTICE", "a.pdf", "1", "brok@x.ru"), "S", "r", default);
        await n.FlushAsync(default);

        var all = mail.Sent.Single(s => s.To.Contains("all@x.ru"));
        Assert.Contains("Системные ошибки (1)", all.Body);
        Assert.Contains("ORA-00942", all.Body);
        Assert.Contains("a.pdf", all.Body);
        Assert.Contains("+ системных ошибок: 1", all.Subject);

        var brok = mail.Sent.Single(s => s.To.Contains("brok@x.ru"));
        Assert.DoesNotContain("ORA-00942", brok.Body);
    }

    [Fact]
    public async Task Only_system_errors_give_a_system_subject()
    {
        var (n, mail) = Create();
        await n.NotifySystemAsync("Папка недоступна", "share down", default);
        await n.FlushAsync(default);
        Assert.Contains("Системные ошибки: 1", Assert.Single(mail.Sent).Subject);
    }

    [Fact]
    public async Task Nothing_collected_means_no_email_and_flush_drains_the_queue()
    {
        var (n, mail) = Create();
        await n.FlushAsync(default);
        Assert.Empty(mail.Sent);

        await n.NotifyFailureAsync(Doc("T", "a.pdf", "1"), "S", "r", default);
        await n.FlushAsync(default);
        await n.FlushAsync(default);                                // второй flush ничего не шлёт повторно
        Assert.Single(mail.Sent);
    }

    [Fact]
    public async Task No_recipients_at_all_sends_nothing_and_does_not_throw()
    {
        var (n, mail) = Create(defaults: "");
        await n.NotifyFailureAsync(Doc("T", "a.pdf", "1"), "S", "r", default);
        await n.FlushAsync(default);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Long_lists_are_capped_but_total_count_is_reported()
    {
        var (n, mail) = Create();
        for (var i = 0; i < DigestNotifier.MaxListed + 25; i++)
            await n.NotifyFailureAsync(Doc("T", $"f{i:D4}.pdf", i.ToString()), "S", "r", default);
        await n.FlushAsync(default);

        var m = Assert.Single(mail.Sent);
        Assert.Contains($"Необработанных документов: {DigestNotifier.MaxListed + 25}", m.Subject);
        Assert.Contains("… и ещё 25 документов", m.Body);
        Assert.Contains("f0000.pdf", m.Body);
        Assert.DoesNotContain($"f{DigestNotifier.MaxListed + 24:D4}.pdf", m.Body);
    }

    [Fact]
    public async Task Smtp_failure_is_swallowed_and_one_group_failing_does_not_block_others()
    {
        var (n, mail) = Create(defaults: "all@x.ru");
        mail.Throw = true;
        await n.NotifyFailureAsync(Doc("T", "a.pdf", "1", "g@x.ru"), "S", "r", default);
        await n.FlushAsync(default);                                // не бросает

        mail.Throw = false;
        await n.NotifyFailureAsync(Doc("T", "a.pdf", "1", "g@x.ru"), "S", "r", default);   // следующий опрос видит то же самое
        await n.FlushAsync(default);
        Assert.NotEmpty(mail.Sent);
    }
}
