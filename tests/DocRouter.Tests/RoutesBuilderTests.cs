using DocRouter.Core;
using DocRouter.Steps;

namespace DocRouter.Tests;

public class RoutesBuilderTests
{
    [Fact]
    public void Template_is_reused_and_step_names_are_unique()
    {
        var b = new RoutesBuilder();
        b.Template("tail", t => t.Step<MoveFile>());
        b.For("A").Step<FindFile>().Step<MoveFile>().UseTemplate("tail");

        var steps = b.Build().Get("A").Steps.Select(s => s.Name).ToArray();
        Assert.Equal(["FindFile", "MoveFile", "MoveFile#2"], steps);
    }

    [Fact]
    public void Configure_is_applied_to_step_instance()
    {
        var b = new RoutesBuilder();
        b.For("A").Step<MoveFile>(s => s.To = Dir.ToSign);
        var def = b.Build().Get("A").Steps[0];

        var step = new MoveFile(new InMemoryFileStore());
        def.Configure!(step);
        Assert.Equal(Dir.ToSign, step.To);
    }

    [Fact]
    public void Unknown_template_throws() =>
        Assert.Throws<InvalidOperationException>(() => new RoutesBuilder().For("A").UseTemplate("nope"));

    [Fact]
    public void Duplicate_route_throws()
    {
        var b = new RoutesBuilder();
        b.For("A").Step<FindFile>();
        Assert.Throws<InvalidOperationException>(() => b.For("A"));
    }

    [Fact]
    public void Empty_route_throws()
    {
        var b = new RoutesBuilder();
        b.For("A");
        Assert.Throws<InvalidOperationException>(() => b.Build());
    }

    [Fact]
    public void Real_routes_build()
    {
        var reg = DocRouter.Routes.DocumentRoutes.Build();
        Assert.Contains("Agreement", reg.DocTypes);
        Assert.Equal("ApplyStamp", reg.Get("Agreement").Steps[1].Name);
    }
}
