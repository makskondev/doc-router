using DocRouter.Core;
using DocRouter.Routes;
using DocRouter.Steps;

namespace DocRouter.Tests;

public class DocumentTypesBuilderTests
{
    [Fact]
    public void Step_names_are_unique_and_configure_is_applied()
    {
        var b = new DocumentTypesBuilder();
        b.Type("A").FromView("V")
            .Step<FindFile>(s => s.In = [Ctx.In])
            .Step<MoveFile>(s => s.To = Ctx.Result)
            .Step<MoveFile>(s => s.To = Ctx.Signed);
        var t = b.Build().All.Single();

        Assert.Equal(new[] { "FindFile", "MoveFile", "MoveFile#2" }, t.Steps.Select(s => s.Name));

        var move = new MoveFile(new InMemoryFileStore());
        t.Steps[2].Configure!(move);
        Assert.Equal(Ctx.Signed, move.To);
    }

    [Fact]
    public void View_defaults_and_code_defaults_to_type_name()
    {
        var b = new DocumentTypesBuilder();
        b.Type("BROK_AGR").FromView("VW_DOCS").Step<FindFile>();
        b.Type("OTHER").FromView("VW_DOCS", code: "07", idColumn: "ID").Step<FindFile>();
        var all = b.Build().All;

        Assert.Equal(new ViewSourceDef("VW_DOCS", "BROK_AGR", "DOC_ID", "FILE_NAME", "DOC_TYPE"), all[0].View);
        Assert.Equal("07", all[1].View!.Code);
        Assert.Equal("ID", all[1].View!.IdColumn);
    }

    [Fact]
    public void NotifyTo_parses_groups_and_removes_duplicates()
    {
        var b = new DocumentTypesBuilder();
        b.Type("A").FromView("V").NotifyTo("a@x.ru; b@x.ru").NotifyTo("B@x.ru,c@x.ru").NotifyTo(null).Step<FindFile>();
        Assert.Equal(new[] { "a@x.ru", "b@x.ru", "c@x.ru" }, b.Build().All.Single().Recipients);
    }

    [Fact]
    public void Several_types_can_share_one_view_with_different_codes()
    {
        var b = new DocumentTypesBuilder();
        b.Type("A").FromView("VW").Step<FindFile>();
        b.Type("B").FromView("VW").Step<FindFile>();
        Assert.Equal(2, b.Build().All.Count);
    }

    [Fact]
    public void Errors_are_reported_all_at_once()
    {
        var b = new DocumentTypesBuilder();
        b.Type("NoSource").Step<FindFile>();
        b.Type("NoSteps").FromView("V");
        b.Type("Dup").FromView("V1").Step<FindFile>();
        b.Type("DUP").FromView("V2").Step<FindFile>();
        b.Type("SameCode1").FromView("V3", code: "1").Step<FindFile>();
        b.Type("SameCode2").FromView("V3", code: "1").Step<FindFile>();

        var ex = Assert.Throws<InvalidOperationException>(() => b.Build());
        Assert.Contains("NoSource", ex.Message);
        Assert.Contains("NoSteps", ex.Message);
        Assert.Contains("описан несколько раз", ex.Message);
        Assert.Contains("SameCode1", ex.Message);
    }

    [Fact]
    public void Source_can_be_set_only_once()
    {
        var t = new DocumentTypesBuilder().Type("A").FromView("V");
        Assert.Throws<InvalidOperationException>(() => t.FromDirectory(Ctx.In));
    }

    [Fact]
    public void Shared_directory_requires_masks_for_every_type_even_with_different_case_and_slash()
    {
        var b = new DocumentTypesBuilder();
        b.Type("A").FromDirectory(@"\\S\R\In").Step<FindFile>();
        b.Type("B").FromDirectory(@"\\s\r\in\", mask: "B_*").Step<FindFile>();

        var ex = Assert.Throws<InvalidOperationException>(() => b.Build());
        Assert.Contains("mask", ex.Message);
        Assert.Contains("A", ex.Message);
    }

    [Fact]
    public void Shared_directory_with_masks_is_ok()
    {
        var b = new DocumentTypesBuilder();
        b.Type("A").FromDirectory(@"\\s\r\in", mask: "A_*").Step<FindFile>();
        b.Type("B").FromDirectory(@"\\s\r\in", mask: "B_*").Step<FindFile>();
        Assert.Equal(2, b.Build().All.Count);
    }

    [Fact]
    public void Real_definitions_build_with_your_three_examples()
    {
        var dirs = new DirectoriesOptions
        {
            Docs = @"\\fs\docs", Signed = @"\\fs\signed", Reports = @"\\fs\reports",
            DepoReportsIn = @"\\fs\in\depo", BrokReportsIn = @"\\fs\in\brok"
        };
        var reg = DocumentDefinitions.Build(dirs, new MailOptions { Default = "d@x", Brokers = "b@x", Depo = "p@x" });

        Assert.True(reg.TryGet("BROK_AGR", out var brok));
        Assert.NotNull(brok.View);                                   // из вью
        Assert.Equal(new[] { "FindFile", "RestSign", "VerifySignature", "SaveSignature" }, brok.Steps.Select(s => s.Name));
        Assert.DoesNotContain(brok.Steps, s => s.StepType == typeof(MoveFile));   // файл остаётся в Docs

        Assert.True(reg.TryGet("DEPO_AGR", out var depo));
        Assert.NotNull(depo.Directory);                              // ждём в Signed
        Assert.True(depo.Directory!.WithSignature);
        Assert.Contains(depo.Steps, s => s.StepType == typeof(DeleteFile));

        Assert.True(reg.TryGet("DEPO_REP", out var depoRep));
        Assert.True(reg.TryGet("BROK_REP", out var brokRep));
        Assert.Contains(depoRep.Steps, s => s.StepType == typeof(RestSign));
        Assert.Equal(new[] { "p@x" }, depoRep.Recipients);
        Assert.Equal(new[] { "b@x" }, brokRep.Recipients);

        // папка-источник у отчётов разная: проверяем, что она «ушла» в шаг FindFile
        var find = new FindFile(new InMemoryFileStore());
        depoRep.Steps[0].Configure!(find);
        Assert.Equal(new[] { dirs.DepoReportsIn }, find.In);
    }
}
