using System.Security.Cryptography;
using DocRouter.Core;
using DocRouter.Steps;

namespace DocRouter.Tests;

public class StepTests
{
    private static readonly byte[] Pdf = "%PDF-1.7 test"u8.ToArray();
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    // ---------- FindFile ----------
    [Fact]
    public async Task FindFile_finds_file_and_remembers_folder()
    {
        var fs = new InMemoryFileStore();
        fs.Add(Paths.Combine(Ctx.In, "a.pdf"), Pdf);
        var ctx = Ctx.Make();

        Assert.IsType<NextResult>(await new FindFile(fs) { In = [Ctx.In] }.ExecuteAsync(ctx, default));
        Assert.Equal(Ctx.In, ctx.State.FoundIn);
        Assert.Equal(Paths.Combine(Ctx.In, "a.pdf"), ctx.State.CurrentPath);
    }

    [Fact]
    public async Task FindFile_respects_the_order_of_search_folders()
    {
        var fs = new InMemoryFileStore();
        fs.Add(Paths.Combine(Ctx.In, "a.pdf"), Pdf);
        fs.Add(Paths.Combine(Ctx.Signed, "a.pdf"), Pdf);

        var ctx = Ctx.Make();
        await new FindFile(fs) { In = [Ctx.Signed, Ctx.In] }.ExecuteAsync(ctx, default);
        Assert.Equal(Ctx.Signed, ctx.State.FoundIn);
    }

    [Fact]
    public async Task FindFile_does_not_look_outside_given_folders_and_fails_when_missing()
    {
        var fs = new InMemoryFileStore();
        fs.Add(Paths.Combine(Ctx.Result, "a.pdf"), Pdf);
        Assert.IsType<FailResult>(await new FindFile(fs) { In = [Ctx.In] }.ExecuteAsync(Ctx.Make(), default));
        Assert.IsType<FailResult>(await new FindFile(fs).ExecuteAsync(Ctx.Make(), default));     // папки не заданы
    }

    private static string[] BrokRepFolders(WorkItem r) =>
        string.Equals(r.Field("REPORT_TYPE"), "pdf", StringComparison.OrdinalIgnoreCase) ? ["SIGNED_DOCS"] :
        r.Field("EMAIL") == "1" ? ["BROK_EMAIL"] : ["BROK_REP"];

    [Theory]
    [InlineData("pdf", "0", "SIGNED_DOCS")]       // pdf — всегда в signed_docs
    [InlineData("PDF", "1", "SIGNED_DOCS")]
    [InlineData("xlsx", "1", "BROK_EMAIL")]       // отправка по email — отдельная папка
    [InlineData("csv", "0", "BROK_REP")]
    public async Task FindFile_resolve_picks_folder_by_record_fields(string type, string email, string expected)
    {
        var fs = new InMemoryFileStore();
        foreach (var f in new[] { "SIGNED_DOCS", "BROK_EMAIL", "BROK_REP" }) fs.Add(Paths.Combine(f, "r.bin"), [1]);
        var item = Ctx.Item(fileName: "r.bin", fields: new Dictionary<string, string> { ["report_type"] = type, ["EMAIL"] = email });
        var ctx = Ctx.Make(fileName: "r.bin", item: item);

        Assert.IsType<NextResult>(await new FindFile(fs) { Resolve = BrokRepFolders }.ExecuteAsync(ctx, default));
        Assert.Equal(expected, ctx.State.FoundIn);       // поле REPORT_TYPE найдено без учёта регистра имени
    }

    // ---------- ExtractClientCode ----------
    [Theory]
    [InlineData("rep_00123_ДО.pdf", "00123")]
    [InlineData("x_7_y_99999_z.xml", "7")]
    public async Task ExtractClientCode_takes_first_digits_between_underscores(string name, string code)
    {
        var ctx = Ctx.Make(fileName: name);
        Assert.IsType<NextResult>(await new ExtractClientCode().ExecuteAsync(ctx, default));
        Assert.Equal(code, ctx.State.ClientCode);
    }

    [Fact]
    public async Task ExtractClientCode_required_fails_optional_passes()
    {
        Assert.IsType<FailResult>(await new ExtractClientCode().ExecuteAsync(Ctx.Make(fileName: "nocode.pdf"), default));
        Assert.IsType<NextResult>(await new ExtractClientCode { Required = false }.ExecuteAsync(Ctx.Make(fileName: "nocode.pdf"), default));
    }

    // ---------- ReadSignatureFile ----------
    [Fact]
    public async Task ReadSignatureFile_reads_neighbour_sig_and_marks_source_file()
    {
        var fs = new InMemoryFileStore();
        var doc = Paths.Combine(Ctx.Signed, "a.pdf");
        fs.Add(doc, Pdf);
        fs.Add(doc + ".sig", [9, 9]);
        var ctx = Ctx.Make(foundIn: Ctx.Signed, currentPath: doc);

        Assert.IsType<NextResult>(await new ReadSignatureFile(fs).ExecuteAsync(ctx, default));
        Assert.Equal(Convert.ToBase64String([9, 9]), ctx.State.SignatureBase64);
        Assert.Equal("file", ctx.State.SignatureSource);
        Assert.Equal(doc + ".sig", ctx.State.Extra["sigPath"]);
    }

    [Fact]
    public async Task ReadSignatureFile_missing_or_empty_sig_fails()
    {
        var fs = new InMemoryFileStore();
        var doc = Paths.Combine(Ctx.Signed, "a.pdf");
        Assert.IsType<FailResult>(await new ReadSignatureFile(fs).ExecuteAsync(Ctx.Make(currentPath: doc), default));

        fs.Add(doc + ".sig", []);
        Assert.IsType<FailResult>(await new ReadSignatureFile(fs).ExecuteAsync(Ctx.Make(currentPath: doc), default));
    }

    // ---------- RestSign + архив ----------
    [Fact]
    public async Task RestSign_signs_when_archive_has_nothing_and_caches_content_and_hash()
    {
        var fs = new InMemoryFileStore();
        var p = Paths.Combine(Ctx.In, "a.pdf");
        fs.Add(p, Pdf);
        var signer = new FakeSigner();
        var ctx = Ctx.Make(currentPath: p);

        Assert.IsType<NextResult>(await new RestSign(fs, signer, new InMemoryArchive()).ExecuteAsync(ctx, default));
        Assert.Equal(1, signer.Signed);
        Assert.Equal(Sha(Pdf), ctx.State.Sha256);
        Assert.Equal("rest", ctx.State.SignatureSource);
        Assert.NotNull(ctx.State.SignatureBase64);
    }

    [Fact]
    public async Task RestSign_reuses_archived_signature_for_same_document_content()
    {
        var fs = new InMemoryFileStore();
        var p = Paths.Combine(Ctx.In, "a.pdf");
        fs.Add(p, Pdf);
        var archive = new InMemoryArchive();
        archive.Rows[("T", "42", Sha(Pdf))] = new ArchiveRecord("T", "View", "42", "a.pdf", null, "k", Sha(Pdf), 1, null, null, [7, 7, 7], "rest");
        var signer = new FakeSigner();
        var ctx = Ctx.Make(currentPath: p);

        Assert.IsType<NextResult>(await new RestSign(fs, signer, archive).ExecuteAsync(ctx, default));
        Assert.Equal(0, signer.Signed);                                   // REST не вызывался
        Assert.Equal(Convert.ToBase64String([7, 7, 7]), ctx.State.SignatureBase64);
    }

    [Fact]
    public async Task RestSign_signs_again_when_file_content_changed()
    {
        var fs = new InMemoryFileStore();
        var p = Paths.Combine(Ctx.In, "a.pdf");
        fs.Add(p, "%PDF-NEW"u8.ToArray());
        var archive = new InMemoryArchive();
        archive.Rows[("T", "42", Sha(Pdf))] = new ArchiveRecord("T", "View", "42", "a.pdf", null, "k", Sha(Pdf), 1, null, null, [7], "rest");
        var signer = new FakeSigner();

        await new RestSign(fs, signer, archive).ExecuteAsync(Ctx.Make(currentPath: p), default);
        Assert.Equal(1, signer.Signed);
    }

    [Fact]
    public async Task RestSign_service_failure_is_a_failure()
    {
        var fs = new InMemoryFileStore();
        var p = Paths.Combine(Ctx.In, "a.pdf");
        fs.Add(p, Pdf);
        Assert.IsType<FailResult>(await new RestSign(fs, new FakeSigner { Throw = true }, new InMemoryArchive())
            .ExecuteAsync(Ctx.Make(currentPath: p), default));
    }

    // ---------- VerifySignature ----------
    [Fact]
    public async Task Verify_passes_invalid_fails_missing_fails()
    {
        var fs = new InMemoryFileStore();
        var p = Paths.Combine(Ctx.In, "a.pdf");
        fs.Add(p, Pdf);
        StepContext WithSig() { var c = Ctx.Make(currentPath: p); c.State.SignatureBase64 = Convert.ToBase64String([1]); return c; }

        Assert.IsType<NextResult>(await new VerifySignature(fs, new FakeSigner()).ExecuteAsync(WithSig(), default));
        Assert.IsType<FailResult>(await new VerifySignature(fs, new FakeSigner { Valid = false }).ExecuteAsync(WithSig(), default));
        Assert.IsType<FailResult>(await new VerifySignature(fs, new FakeSigner()).ExecuteAsync(Ctx.Make(currentPath: p), default));
    }

    // ---------- PlanTargetPath / MoveFile ----------
    [Fact]
    public async Task PlanTargetPath_keeps_original_name_when_free()
    {
        var ctx = Ctx.Make(fileName: "a.pdf");
        Assert.IsType<NextResult>(await new PlanTargetPath(new InMemoryFileStore()) { To = Ctx.Result }.ExecuteAsync(ctx, default));
        Assert.Equal(Paths.Combine(Ctx.Result, "a.pdf"), ctx.State.PlannedPath);
    }

    [Fact]
    public async Task PlanTargetPath_collision_gives_deterministic_suffix_from_source_mtime()
    {
        var fs = new InMemoryFileStore();
        fs.Add(Paths.Combine(Ctx.Result, "a.pdf"), [0]);
        var mtime = new DateTime(2026, 11, 9, 15, 30, 45, DateTimeKind.Utc);
        var item = Ctx.Item(fields: new Dictionary<string, string> { ["SourceModifiedUtc"] = mtime.ToString("O") });

        var c1 = Ctx.Make(item: item);
        var c2 = Ctx.Make(item: item);
        var step = new PlanTargetPath(fs) { To = Ctx.Result };
        await step.ExecuteAsync(c1, default);
        await step.ExecuteAsync(c2, default);

        Assert.Equal(Paths.Combine(Ctx.Result, "a_20261109_153045.pdf"), c1.State.PlannedPath);
        Assert.Equal(c1.State.PlannedPath, c2.State.PlannedPath);        // повтор после сбоя даёт тот же путь
    }

    [Fact]
    public async Task PlanTargetPath_second_collision_adds_counter()
    {
        var fs = new InMemoryFileStore();
        var mtime = new DateTime(2026, 11, 9, 15, 30, 45, DateTimeKind.Utc);
        fs.Add(Paths.Combine(Ctx.Result, "a.pdf"), [0]);
        fs.Add(Paths.Combine(Ctx.Result, "a_20261109_153045.pdf"), [0]);
        var item = Ctx.Item(fields: new Dictionary<string, string> { ["SourceModifiedUtc"] = mtime.ToString("O") });
        var ctx = Ctx.Make(item: item);

        await new PlanTargetPath(fs) { To = Ctx.Result }.ExecuteAsync(ctx, default);
        Assert.Equal(Paths.Combine(Ctx.Result, "a_20261109_153045_1.pdf"), ctx.State.PlannedPath);
    }

    [Fact]
    public async Task MoveFile_moves_exactly_to_planned_path_and_never_overwrites()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Ctx.Signed, "a.pdf");
        var planned = Paths.Combine(Ctx.Result, "a.pdf");
        fs.Add(src, Pdf);
        var ctx = Ctx.Make(currentPath: src);
        ctx.State.PlannedPath = planned;

        Assert.IsType<NextResult>(await new MoveFile(fs).ExecuteAsync(ctx, default));
        Assert.True(fs.Has(planned));
        Assert.False(fs.Has(src));
        Assert.Equal(planned, ctx.State.CurrentPath);
    }

    [Fact]
    public async Task MoveFile_without_plan_plans_by_To_and_fails_without_anything()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Ctx.Signed, "a.pdf");
        fs.Add(src, Pdf);
        var ctx = Ctx.Make(currentPath: src);
        Assert.IsType<NextResult>(await new MoveFile(fs) { To = Ctx.Result }.ExecuteAsync(ctx, default));
        Assert.Equal(Paths.Combine(Ctx.Result, "a.pdf"), ctx.State.CurrentPath);

        Assert.IsType<FailResult>(await new MoveFile(new InMemoryFileStore()).ExecuteAsync(Ctx.Make(currentPath: src), default));   // нет файла
    }

    // ---------- ArchiveSignature ----------
    [Fact]
    public async Task ArchiveSignature_stores_signature_and_metadata_idempotently()
    {
        var fs = new InMemoryFileStore();
        var p = Paths.Combine(Ctx.In, "rep_00123_ДО.pdf");
        fs.Add(p, Pdf);
        var archive = new InMemoryArchive();
        var item = Ctx.Item(docType: "DEPO_REP_DO", fileName: "rep_00123_ДО.pdf", kind: "ДО");

        StepContext NewCtx()
        {
            var c = Ctx.Make(fileName: "rep_00123_ДО.pdf", currentPath: p, item: item);
            c.State.SignatureBase64 = Convert.ToBase64String([1, 2]);
            c.State.SignatureSource = "rest";
            c.State.ClientCode = "00123";
            c.State.PlannedPath = Paths.Combine(Ctx.Result, "rep_00123_ДО.pdf");
            return c;
        }

        var step = new ArchiveSignature(fs, archive);
        Assert.IsType<NextResult>(await step.ExecuteAsync(NewCtx(), default));
        Assert.IsType<NextResult>(await step.ExecuteAsync(NewCtx(), default));      // повтор не дублирует

        var row = Assert.Single(archive.Rows.Values);
        Assert.Equal(("DEPO_REP_DO", "View", "42"), (row.DocType, row.SourceKind, row.SourceKey));
        Assert.Equal(("00123", "ДО", Sha(Pdf), (long)Pdf.Length), (row.ClientCode, row.Kind, row.Sha256, row.FileSize));
        Assert.Equal(Paths.Combine(Ctx.Result, "rep_00123_ДО.pdf"), row.TargetPath);
        Assert.Equal(new byte[] { 1, 2 }, row.Signature);
    }

    [Fact]
    public async Task ArchiveSignature_takes_client_and_kind_from_view_fields()
    {
        var fs = new InMemoryFileStore();
        var p = Paths.Combine(Ctx.In, "r.xlsx");
        fs.Add(p, [1, 2, 3]);
        var archive = new InMemoryArchive();
        var item = Ctx.Item(docType: "BROK_REP", fileName: "r.xlsx", fields: new Dictionary<string, string> { ["CLIENT_ID"] = "C-77", ["REPORT_TYPE"] = "xlsx" });
        var ctx = Ctx.Make(fileName: "r.xlsx", currentPath: p, item: item);
        ctx.State.SignatureBase64 = Convert.ToBase64String([5]);

        await new ArchiveSignature(fs, archive) { ClientField = "CLIENT_ID", KindField = "REPORT_TYPE" }.ExecuteAsync(ctx, default);

        var row = Assert.Single(archive.Rows.Values);
        Assert.Equal(("C-77", "xlsx"), (row.ClientCode, row.Kind));
        Assert.Null(row.TargetPath);                         // у отчёта файл не перемещается
    }

    [Fact]
    public async Task ArchiveSignature_without_signature_fails() =>
        Assert.IsType<FailResult>(await new ArchiveSignature(new InMemoryFileStore(), new InMemoryArchive())
            .ExecuteAsync(Ctx.Make(currentPath: "x"), default));

    // ---------- Oracle ----------
    [Fact]
    public async Task MarkSigned_calls_single_procedure_with_record_id_and_signature()
    {
        var store = new FakeSignatureStore();
        var ctx = Ctx.Make(item: Ctx.Item(id: "R-5"));
        ctx.State.SignatureBase64 = Convert.ToBase64String([4, 2]);

        Assert.IsType<NextResult>(await new MarkSigned(store).ExecuteAsync(ctx, default));
        var m = Assert.Single(store.Marked);
        Assert.Equal("R-5", m.Id);
        Assert.Equal(new byte[] { 4, 2 }, m.Sig);
        Assert.IsType<FailResult>(await new MarkSigned(store).ExecuteAsync(Ctx.Make(), default));   // подписи нет
    }

    [Fact]
    public async Task CreateFileRecord_passes_name_kind_client_saved_path_and_signature()
    {
        var store = new FakeSignatureStore();
        var item = Ctx.Item(docType: "DEPO_REP_JRN", fileName: "a_00123_жрн.pdf", kind: "жрн");
        var ctx = Ctx.Make(fileName: "a_00123_жрн.pdf", item: item);
        ctx.State.SignatureBase64 = Convert.ToBase64String([1]);
        ctx.State.ClientCode = "00123";
        ctx.State.PlannedPath = Paths.Combine(Ctx.Result, "a_00123_жрн.pdf");

        Assert.IsType<NextResult>(await new CreateFileRecord(store).ExecuteAsync(ctx, default));
        var r = Assert.Single(store.Records);
        Assert.Equal(("a_00123_жрн.pdf", "жрн", "00123", Paths.Combine(Ctx.Result, "a_00123_жрн.pdf")), (r.FileName, r.Kind, r.ClientCode, r.SavedPath));
        Assert.Equal(new byte[] { 1 }, r.Signature);
    }

    [Fact]
    public async Task CreateFileRecord_requires_planned_path()
    {
        var ctx = Ctx.Make();
        ctx.State.SignatureBase64 = Convert.ToBase64String([1]);
        Assert.IsType<FailResult>(await new CreateFileRecord(new FakeSignatureStore()).ExecuteAsync(ctx, default));
    }

    // ---------- DeleteFile ----------
    [Fact]
    public async Task DeleteFile_is_idempotent()
    {
        var fs = new InMemoryFileStore();
        var sig = Paths.Combine(Ctx.Signed, "a.pdf.sig");
        fs.Add(sig, [1]);
        var ctx = Ctx.Make();
        ctx.State.Extra["sigPath"] = sig;
        var step = new DeleteFile(fs);

        Assert.IsType<NextResult>(await step.ExecuteAsync(ctx, default));
        Assert.False(fs.Has(sig));
        Assert.IsType<NextResult>(await step.ExecuteAsync(ctx, default));
    }
}

public class ManualSigningStepTests
{
    private const string Docs = @"\\fd\brok\docs";
    private const string ToSign = @"\\fd\sign_proc\to_sign\brok";
    private const string Signed = @"\\fd\sign_proc\was_signed\brok";
    private static readonly byte[] Pdf = "%PDF-1.7 app"u8.ToArray();

    private static TestTime Clock() => new(new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc));

    // ---------- SendToManualSigning ----------
    [Fact]
    public async Task Send_moves_file_to_manual_signing_folder_and_remembers_the_moment()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Docs, "app.pdf");
        fs.Add(src, Pdf);
        var time = Clock();
        var ctx = Ctx.Make(fileName: "app.pdf", foundIn: Docs, currentPath: src);

        Assert.IsType<NextResult>(await new SendToManualSigning(fs, time) { From = Docs, To = ToSign }.ExecuteAsync(ctx, default));

        Assert.False(fs.Has(src));
        Assert.True(fs.Has(Paths.Combine(ToSign, "app.pdf")));
        Assert.Equal(ToSign, ctx.State.FoundIn);
        Assert.Equal(time.Now.ToString("O"), ctx.State.Extra[SendToManualSigning.SentAtKey]);
    }

    [Fact]
    public async Task Send_is_safe_to_repeat_when_file_is_already_in_manual_signing_folder()
    {
        var fs = new InMemoryFileStore();
        var there = Paths.Combine(ToSign, "app.pdf");
        fs.Add(there, Pdf);
        var ctx = Ctx.Make(fileName: "app.pdf", foundIn: ToSign, currentPath: there);

        Assert.IsType<NextResult>(await new SendToManualSigning(fs, Clock()) { From = Docs, To = ToSign }.ExecuteAsync(ctx, default));
        Assert.True(ctx.State.Extra.ContainsKey(SendToManualSigning.SentAtKey));            // момент зафиксирован после сбоя
        Assert.True(fs.Has(there));
    }

    [Fact]
    public async Task Send_keeps_original_sent_moment_on_repeat()
    {
        var fs = new InMemoryFileStore();
        var there = Paths.Combine(ToSign, "app.pdf");
        fs.Add(there, Pdf);
        var ctx = Ctx.Make(fileName: "app.pdf", foundIn: ToSign, currentPath: there);
        ctx.State.Extra[SendToManualSigning.SentAtKey] = "2026-11-10T08:00:00.0000000Z";

        await new SendToManualSigning(fs, Clock()) { From = Docs, To = ToSign }.ExecuteAsync(ctx, default);
        Assert.Equal("2026-11-10T08:00:00.0000000Z", ctx.State.Extra[SendToManualSigning.SentAtKey]);
    }

    [Fact]
    public async Task Send_never_overwrites_a_file_waiting_in_manual_signing_folder()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Docs, "app.pdf");
        fs.Add(src, Pdf);
        fs.Add(Paths.Combine(ToSign, "app.pdf"), [9]);              // чужой файл с тем же именем ждёт подписи
        var ctx = Ctx.Make(fileName: "app.pdf", foundIn: Docs, currentPath: src);

        Assert.IsType<FailResult>(await new SendToManualSigning(fs, Clock()) { From = Docs, To = ToSign }.ExecuteAsync(ctx, default));
        Assert.True(fs.Has(src));
        Assert.Equal(new byte[] { 9 }, fs.Get(Paths.Combine(ToSign, "app.pdf")));
    }

    [Fact]
    public async Task Send_fails_on_unexpected_folder_or_missing_settings()
    {
        var fs = new InMemoryFileStore();
        Assert.IsType<FailResult>(await new SendToManualSigning(fs, Clock()) { From = Docs, To = ToSign }
            .ExecuteAsync(Ctx.Make(foundIn: Signed, currentPath: "x"), default));
        Assert.IsType<FailResult>(await new SendToManualSigning(fs, Clock()).ExecuteAsync(Ctx.Make(foundIn: Docs), default));
    }

    // ---------- WaitForManualSignature ----------
    private static WaitForManualSignature Wait(InMemoryFileStore fs, TestTime time) =>
        new(fs, time) { Signed = Signed, Timeout = TimeSpan.FromHours(24), StableFor = TimeSpan.FromSeconds(30) };

    private static StepContext Waiting(TestTime time, TimeSpan sentAgo)
    {
        var ctx = Ctx.Make(fileName: "app.pdf", foundIn: ToSign, currentPath: Paths.Combine(ToSign, "app.pdf"));
        ctx.State.Extra[SendToManualSigning.SentAtKey] = (time.Now - sentAgo).ToString("O");
        return ctx;
    }

    [Fact]
    public async Task Wait_nothing_yet_waits_quietly_within_24_hours()
    {
        var time = Clock();
        Assert.IsType<WaitResult>(await Wait(new InMemoryFileStore(), time).ExecuteAsync(Waiting(time, TimeSpan.FromHours(3)), default));
    }

    [Fact]
    public async Task Wait_fails_after_24_hours_counted_from_the_sent_moment_not_from_file_time()
    {
        var time = Clock();
        var fs = new InMemoryFileStore();
        fs.Add(Paths.Combine(ToSign, "app.pdf"), Pdf, time.Now);          // свежий mtime файла не должен сбрасывать отсчёт

        Assert.IsType<FailResult>(await Wait(fs, time).ExecuteAsync(Waiting(time, TimeSpan.FromHours(25)), default));
    }

    [Fact]
    public async Task Wait_with_stable_signed_pair_moves_on_and_switches_to_signed_file()
    {
        var time = Clock();
        var fs = new InMemoryFileStore();
        var doc = Paths.Combine(Signed, "app.pdf");
        fs.Add(doc, Pdf, time.Now.AddMinutes(-5));
        fs.Add(doc + ".sig", [1], time.Now.AddMinutes(-5));
        var ctx = Waiting(time, TimeSpan.FromHours(2));
        ctx.State.Content = [1, 2, 3];

        Assert.IsType<NextResult>(await Wait(fs, time).ExecuteAsync(ctx, default));
        Assert.Equal(doc, ctx.State.CurrentPath);
        Assert.Equal(Signed, ctx.State.FoundIn);
        Assert.Equal(doc + ".sig", ctx.State.Extra["sigPath"]);
        Assert.Null(ctx.State.Content);                                    // содержимое подписанного файла будет прочитано заново
    }

    [Fact]
    public async Task Wait_arrived_signed_pair_wins_even_after_the_24h_deadline()
    {
        var time = Clock();
        var fs = new InMemoryFileStore();
        var doc = Paths.Combine(Signed, "app.pdf");
        fs.Add(doc, Pdf, time.Now.AddMinutes(-5));
        fs.Add(doc + ".sig", [1], time.Now.AddMinutes(-5));

        Assert.IsType<NextResult>(await Wait(fs, time).ExecuteAsync(Waiting(time, TimeSpan.FromHours(40)), default));
    }

    [Fact]
    public async Task Wait_fresh_or_incomplete_pair_keeps_waiting()
    {
        var time = Clock();
        var fs = new InMemoryFileStore();
        var doc = Paths.Combine(Signed, "app.pdf");
        fs.Add(doc, Pdf, time.Now.AddMinutes(-5));                         // подписи ещё нет
        Assert.IsType<WaitResult>(await Wait(fs, time).ExecuteAsync(Waiting(time, TimeSpan.FromHours(1)), default));

        fs.Add(doc + ".sig", [1], time.Now.AddSeconds(-5));                // подпись ещё дописывается
        Assert.IsType<WaitResult>(await Wait(fs, time).ExecuteAsync(Waiting(time, TimeSpan.FromHours(1)), default));
    }

    [Fact]
    public async Task Wait_without_known_sent_moment_starts_the_clock_instead_of_failing()
    {
        var time = Clock();
        var ctx = Ctx.Make(fileName: "app.pdf", foundIn: ToSign);
        Assert.IsType<WaitResult>(await Wait(new InMemoryFileStore(), time).ExecuteAsync(ctx, default));
        Assert.True(ctx.State.Extra.ContainsKey(SendToManualSigning.SentAtKey));
    }
}
