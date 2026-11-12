using DocRouter.Core;
using DocRouter.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocRouter.Tests;

public class CourierWorkerTests
{
    private sealed class FakeSource(params WorkItem[] items) : ISourcePoller
    {
        public string Name => "fake";
        public Task<IReadOnlyList<WorkItem>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WorkItem>>(items);
    }

    private sealed class BoomSource : ISourcePoller
    {
        public string Name => "boom";
        public Task<IReadOnlyList<WorkItem>> DiscoverAsync(CancellationToken ct) => throw new IOException("view down");
    }

    private static (CourierWorker worker, CapturingMailSender mail) Create(IEnumerable<ISourcePoller> sources)
    {
        var b = new DocumentTypesBuilder();
        b.Type("T").FromView("V").NotifyTo("team@x.ru").Step<FailingStep>();
        var registry = b.Build();

        var mail = new CapturingMailSender();
        var mailOptions = Options.Create(new MailOptions { Default = "all@x.ru" });
        var notifier = new DigestNotifier(mail, mailOptions, TimeProvider.System, NullLogger<DigestNotifier>.Instance);
        var sp = new ServiceCollection().AddSingleton(new Trace()).BuildServiceProvider();
        var runner = new ItemRunner(sp, registry, notifier, new InMemoryStateStore(), new RecordingJournal(), NullLogger<ItemRunner>.Instance);

        var worker = new CourierWorker(sources, runner, notifier, new CourierStatus(TimeProvider.System), TimeProvider.System,
            Options.Create(new CourierOptions { MaxParallel = 4 }), NullLogger<CourierWorker>.Instance);
        return (worker, mail);
    }

    private static WorkItem Item(string file, string id) => new()
    {
        DocType = "T", FileName = file, ExternalId = id, Source = SourceKind.View, Recipients = ["team@x.ru"]
    };

    [Fact]
    public async Task Poll_with_many_unprocessed_files_sends_one_email_not_one_per_file()
    {
        var (worker, mail) = Create([new FakeSource(Item("a.pdf", "1"), Item("b.pdf", "2"), Item("c.pdf", "3"))]);

        await worker.CycleAsync(default);

        // у team@x.ru и all@x.ru одинаковый набор ошибок → одно письмо на обоих
        var m = Assert.Single(mail.Sent);
        Assert.Equal(new[] { "all@x.ru", "team@x.ru" }, m.To.OrderBy(x => x));
        Assert.Contains("Необработанных документов: 3", m.Subject);
        Assert.Contains("a.pdf", m.Body);
        Assert.Contains("b.pdf", m.Body);
        Assert.Contains("c.pdf", m.Body);
        Assert.Contains("bad signature", m.Body);                               // причина указана
    }

    [Fact]
    public async Task Source_failure_is_reported_in_the_same_digest_and_other_sources_still_work()
    {
        var (worker, mail) = Create([new BoomSource(), new FakeSource(Item("a.pdf", "1"))]);

        await worker.CycleAsync(default);

        var all = mail.Sent.Single(s => s.To.Contains("all@x.ru"));
        Assert.Contains("a.pdf", all.Body);
        Assert.Contains("Системные ошибки (1)", all.Body);
        Assert.Contains("view down", all.Body);
    }

    [Fact]
    public async Task Healthy_poll_sends_nothing()
    {
        var (worker, mail) = Create([new FakeSource()]);
        await worker.CycleAsync(default);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Same_problem_is_reported_again_on_the_next_poll()
    {
        var (worker, mail) = Create([new FakeSource(Item("a.pdf", "1"))]);

        await worker.CycleAsync(default);
        var first = mail.Sent.Count;
        await worker.CycleAsync(default);

        Assert.Equal(first * 2, mail.Sent.Count);                               // раз в опрос, пока проблема остаётся
    }
}
