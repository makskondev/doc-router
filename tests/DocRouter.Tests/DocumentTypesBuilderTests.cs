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
    public void View_without_type_column_cannot_share_the_view()
    {
        var b = new DocumentTypesBuilder();
        b.Type("ALL").FromView("VW", typeColumn: null).Step<FindFile>();
        b.Type("ONE").FromView("VW", code: "1").Step<FindFile>();
        Assert.Contains("typeColumn", Assert.Throws<InvalidOperationException>(() => b.Build()).Message);
    }

    [Fact]
    public void Kind_defaults_to_type_name_and_can_be_overridden()
    {
        var b = new DocumentTypesBuilder();
        b.Type("A").FromView("V1").Step<FindFile>();
        b.Type("B").Kind("ДО").FromView("V2").Step<FindFile>();
        var all = b.Build().All;
        Assert.Equal(new[] { "A", "ДО" }, all.Select(t => t.Kind));
    }

    private static DirectoriesOptions Dirs() => new()
    {
        BrokRep = @"\\fd\brok_rep", BrokRepEmail = @"\\fd\brok_rep\email", SignedDocs = @"\\fd\signed_docs",
        DepoReports = @"\\fd\depo_reports", WasSignedDepo = @"\\fd\sign_proc\was_signed\depo", WasSignedBrok = @"\\fd\sign_proc\was_signed\brok"
    };

    private static DocumentTypeRegistry Real() =>
        DocumentDefinitions.Build(Dirs(), new MailOptions { Default = "d@x", Brokers = "b@x", Depo = "p@x" });

    [Fact]
    public void Real_definitions_contain_all_document_types_of_four_families()
    {
        var names = Real().All.Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Equal(new[]
        {
            "BROK_APPLICATION", "BROK_NOTICE", "BROK_REP", "DEPO_APPLICATION", "DEPO_NOTICE", "DEPO_REP_DO", "DEPO_REP_JRN"
        }, names);
    }

    [Fact]
    public void Family1_broker_reports_come_from_view_do_not_move_and_finish_with_single_oracle_call()
    {
        Assert.True(Real().TryGet("BROK_REP", out var t));
        Assert.Equal("report_log_vw", t.View!.View);
        Assert.Null(t.View.TypeColumn);
        Assert.Equal(new[] { "FindFile", "RestSign", "VerifySignature", "ArchiveSignature", "MarkReportSigned" }, t.Steps.Select(s => s.Name));
        Assert.DoesNotContain(t.Steps, s => s.StepType == typeof(MoveFile));
        Assert.Equal(new[] { "b@x" }, t.Recipients);
    }

    [Theory]
    [InlineData("DEPO_REP_DO", "ДО", "*_ДО.*")]
    [InlineData("DEPO_REP_JRN", "жрн", "*_жрн.*")]
    public void Family2_depo_reports_share_folder_split_by_postfix_and_take_client_from_name(string type, string kind, string mask)
    {
        Assert.True(Real().TryGet(type, out var t));
        Assert.Equal(kind, t.Kind);
        Assert.Equal(mask, t.Directory!.Mask);
        Assert.False(t.Directory.WithSignature);
        Assert.Equal(new[] { "FindFile", "ExtractClientCode", "RestSign", "VerifySignature", "PlanTargetPath", "ArchiveSignature", "CreateFileRecord", "MoveFile" },
            t.Steps.Select(s => s.Name));
        Assert.True(FileMask.IsMatch(mask, "rep_00123_" + kind + ".pdf"));
    }

    [Theory]
    [InlineData("DEPO_NOTICE")]
    [InlineData("DEPO_APPLICATION")]
    [InlineData("BROK_NOTICE")]
    [InlineData("BROK_APPLICATION")]
    public void Families_3_and_4_documents_arrive_with_sig_wait_24h_and_move_last(string type)
    {
        Assert.True(Real().TryGet(type, out var t));
        Assert.True(t.Directory!.WithSignature);
        Assert.Equal(TimeSpan.FromHours(24), t.Directory.SignatureTimeout);
        Assert.Equal(new[] { "FindFile", "ReadSignatureFile", "VerifySignature", "PlanTargetPath", "ArchiveSignature", "CreateFileRecord", "MoveFile", "DeleteFile" },
            t.Steps.Select(s => s.Name));
        // порядок критичен: Oracle и архив — ДО перемещения (триггер — сам файл)
        var order = t.Steps.Select(s => s.StepType).ToList();
        Assert.True(order.IndexOf(typeof(CreateFileRecord)) < order.IndexOf(typeof(MoveFile)));
        Assert.True(order.IndexOf(typeof(ArchiveSignature)) < order.IndexOf(typeof(CreateFileRecord)));
    }

    [Fact]
    public void Folders_are_passed_into_steps_as_values()
    {
        var dirs = Dirs();
        var reg = Real();

        Assert.True(reg.TryGet("DEPO_NOTICE", out var depo));
        var find = new FindFile(new InMemoryFileStore());
        depo.Steps[0].Configure!(find);
        Assert.Equal(new[] { dirs.WasSignedDepo }, find.In);

        var plan = new PlanTargetPath(new InMemoryFileStore());
        depo.Steps.Single(s => s.StepType == typeof(PlanTargetPath)).Configure!(plan);
        Assert.Equal(dirs.SignedDocs, plan.To);

        Assert.True(reg.TryGet("BROK_NOTICE", out var brok));
        var find2 = new FindFile(new InMemoryFileStore());
        brok.Steps[0].Configure!(find2);
        Assert.Equal(new[] { dirs.WasSignedBrok }, find2.In);
    }

    [Fact]
    public void Shared_source_folders_are_split_by_masks_so_registry_is_valid()
    {
        var reg = Real();
        var depoDocs = reg.All.Where(t => t.Directory is not null && Paths.SameDir(t.Directory.Directory, Dirs().WasSignedDepo)).ToList();
        Assert.Equal(2, depoDocs.Count);                                    // извещения и заявления делят одну папку
        Assert.All(depoDocs, t => Assert.False(string.IsNullOrWhiteSpace(t.Directory!.Mask)));
    }
}
