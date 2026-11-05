using DocRouter.Core;
using DocRouter.Engine;
using DocRouter.Steps;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocRouter.Tests;

public class ViewSourceTests
{
    private sealed class FakeViewReader(Dictionary<string, List<Dictionary<string, string?>>> views) : IViewReader
    {
        public List<string> Reads { get; } = new();

        public Task<IReadOnlyList<ViewRecord>> ReadAsync(string viewName, CancellationToken ct)
        {
            Reads.Add(viewName);
            if (!views.TryGetValue(viewName, out var rows)) throw new IOException("ORA-00942: view does not exist");
            return Task.FromResult<IReadOnlyList<ViewRecord>>(rows.Select(r => new ViewRecord(r)).ToList());
        }

        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static Dictionary<string, string?> Row(string? id, string? file, string? type) =>
        new(StringComparer.OrdinalIgnoreCase) { ["DOC_ID"] = id, ["FILE_NAME"] = file, ["DOC_TYPE"] = type, ["AUTHOR"] = "Иванов" };

    private static (ViewSource src, CapturingNotifier n, FakeViewReader r) Create(
        Action<DocumentTypesBuilder> define, Dictionary<string, List<Dictionary<string, string?>>> views)
    {
        var b = new DocumentTypesBuilder();
        define(b);
        var reader = new FakeViewReader(views);
        var n = new CapturingNotifier();
        return (new ViewSource(reader, n, b.Build(), NullLogger<ViewSource>.Instance), n, reader);
    }

    [Fact]
    public async Task One_view_with_several_types_is_read_once_and_split_by_code()
    {
        var (src, n, reader) = Create(b =>
        {
            b.Type("BROK_AGR").FromView("VW").NotifyTo("b@x.ru").Step<FindFile>();
            b.Type("DEPO_REP").FromView("VW", code: "07").NotifyTo("d@x.ru").Step<FindFile>();
        }, new() { ["VW"] = [Row("1", "a.pdf", "BROK_AGR"), Row("2", "b.pdf", "07"), Row("3", "c.pdf", "brok_agr")] });

        var items = await src.DiscoverAsync(default);

        Assert.Equal(new[] { "VW" }, reader.Reads);
        Assert.Equal(new[] { "BROK_AGR", "BROK_AGR", "DEPO_REP" }, items.Select(i => i.DocType).OrderBy(x => x));
        var depo = items.Single(i => i.DocType == "DEPO_REP");
        Assert.Equal("2", depo.ExternalId);
        Assert.Equal(new[] { "d@x.ru" }, depo.Recipients);
        Assert.Equal("Иванов", depo.Fields["AUTHOR"]);
        Assert.Empty(n.System);
    }

    [Fact]
    public async Task Codes_we_do_not_handle_are_skipped_without_emails()
    {
        var (src, n, _) = Create(
            b => b.Type("BROK_AGR").FromView("VW").Step<FindFile>(),
            new() { ["VW"] = [Row("1", "a.pdf", "BROK_AGR"), Row("2", "x.pdf", "SOMETHING_ELSE"), Row("3", "y.pdf", null)] });

        var items = await src.DiscoverAsync(default);

        Assert.Equal(new[] { "1" }, items.Select(i => i.ExternalId));
        Assert.Empty(n.System);
    }

    [Fact]
    public async Task Code_is_trimmed_for_char_columns()
    {
        var (src, _, _) = Create(b => b.Type("A").FromView("VW").Step<FindFile>(), new() { ["VW"] = [Row("1", "a.pdf", "A   ")] });
        Assert.Single(await src.DiscoverAsync(default));
    }

    [Fact]
    public async Task Record_of_our_type_without_id_or_file_name_sends_system_email()
    {
        var (src, n, _) = Create(b => b.Type("A").FromView("VW").Step<FindFile>(),
            new() { ["VW"] = [Row(null, "a.pdf", "A"), Row("2", null, "A"), Row("3", "c.pdf", "A")] });

        Assert.Equal(new[] { "3" }, (await src.DiscoverAsync(default)).Select(i => i.ExternalId));
        Assert.Equal(2, n.System.Count);
    }

    [Fact]
    public async Task Unreadable_view_sends_email_and_does_not_block_other_views()
    {
        var (src, n, _) = Create(b =>
        {
            b.Type("A").FromView("MISSING").Step<FindFile>();
            b.Type("B").FromView("VW").Step<FindFile>();
        }, new() { ["VW"] = [Row("1", "a.pdf", "B")] });

        var items = await src.DiscoverAsync(default);

        Assert.Equal(new[] { "B" }, items.Select(i => i.DocType));
        Assert.Single(n.System);
    }

    [Fact]
    public async Task Custom_columns_are_used()
    {
        var rows = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ID"] = "9", ["NAME"] = "z.pdf", ["KIND"] = "A" };
        var (src, _, _) = Create(
            b => b.Type("A").FromView("VW", idColumn: "ID", nameColumn: "NAME", typeColumn: "KIND").Step<FindFile>(),
            new() { ["VW"] = [rows] });

        var i = Assert.Single(await src.DiscoverAsync(default));
        Assert.Equal(("9", "z.pdf"), (i.ExternalId, i.FileName));
    }
}
