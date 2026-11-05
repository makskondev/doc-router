using DocRouter.Core;
using DocRouter.Engine;
using DocRouter.Steps;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocRouter.Tests;

public class ViewSourceTests
{
    private sealed class FakeViewReader(params Dictionary<string, string?>[] rows) : IViewReader
    {
        public Task<IReadOnlyList<ViewRecord>> ReadAsync(string viewName, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ViewRecord>>(rows.Select(r => new ViewRecord(r)).ToList());
        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static Dictionary<string, string?> Row(string? id, string? file, string? type) =>
        new(StringComparer.OrdinalIgnoreCase) { ["DOC_ID"] = id, ["FILE_NAME"] = file, ["DOC_TYPE"] = type, ["AUTHOR"] = "Иванов" };

    private static (ViewSource src, CapturingNotifier n) Create(ViewOptions view, params Dictionary<string, string?>[] rows)
    {
        var b = new RoutesBuilder();
        b.For("Invoice").Step<FindFile>();
        b.For("Act").Step<FindFile>();
        var n = new CapturingNotifier();
        var sources = new SourcesOptions { Views = [view] };
        return (new ViewSource(new FakeViewReader(rows), n, b.Build(), Options.Create(sources), NullLogger<ViewSource>.Instance), n);
    }

    [Fact]
    public async Task Fixed_route_ignores_type_column()
    {
        var (src, n) = Create(new ViewOptions { Name = "V", Route = "Invoice" }, Row("1", "a.pdf", "zzz"));
        var items = await src.DiscoverAsync(default);

        var i = Assert.Single(items);
        Assert.Equal("Invoice", i.Route);
        Assert.Equal("1", i.ExternalId);
        Assert.Equal("Иванов", i.Fields["AUTHOR"]);
        Assert.Empty(n.System);
    }

    [Fact]
    public async Task Type_codes_are_mapped_to_routes_case_insensitive_and_trimmed()
    {
        var view = new ViewOptions { Name = "V", TypeMap = new() { ["01"] = "Invoice", ["ab"] = "Act" } };
        var (src, n) = Create(view, Row("1", "a.pdf", "01 "), Row("2", "b.pdf", "AB"));
        var items = await src.DiscoverAsync(default);

        Assert.Equal(new[] { "Invoice", "Act" }, items.Select(x => x.Route));
        Assert.Empty(n.System);
    }

    [Fact]
    public async Task Unknown_code_sends_system_email_and_skips_record()
    {
        var view = new ViewOptions { Name = "V", TypeMap = new() { ["01"] = "Invoice" } };
        var (src, n) = Create(view, Row("1", "a.pdf", "99"), Row("2", "b.pdf", "01"));
        var items = await src.DiscoverAsync(default);

        Assert.Equal(new[] { "2" }, items.Select(x => x.ExternalId));
        var mail = Assert.Single(n.System);
        Assert.Contains("99", mail);
    }

    [Fact]
    public async Task Without_map_code_is_route_name()
    {
        var (src, _) = Create(new ViewOptions { Name = "V" }, Row("1", "a.pdf", "Act"));
        Assert.Equal("Act", Assert.Single(await src.DiscoverAsync(default)).Route);
    }

    [Fact]
    public async Task Code_without_route_in_code_sends_email()
    {
        var (src, n) = Create(new ViewOptions { Name = "V" }, Row("1", "a.pdf", "Nope"));
        Assert.Empty(await src.DiscoverAsync(default));
        Assert.Single(n.System);
    }

    [Fact]
    public async Task Record_without_id_or_type_sends_email()
    {
        var (src, n) = Create(new ViewOptions { Name = "V" }, Row(null, "a.pdf", "Act"), Row("2", "b.pdf", null));
        Assert.Empty(await src.DiscoverAsync(default));
        Assert.Equal(2, n.System.Count);
    }
}
