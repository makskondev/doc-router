using DocRouter.Core;
using DocRouter.Steps;

namespace DocRouter.Tests;

public class StepTests
{
    private static readonly byte[] Pdf = "%PDF-1.7 test"u8.ToArray();

    // ---------- FindFile ----------
    [Fact]
    public async Task FindFile_not_found_waits_and_notifies_only_once()
    {
        var notifier = new CapturingNotifier();
        var step = new FindFile(new InMemoryFileStore(), notifier);
        var ctx = Ctx.Make();

        Assert.IsType<WaitResult>(await step.ExecuteAsync(ctx, default));
        Assert.IsType<WaitResult>(await step.ExecuteAsync(ctx, default));
        Assert.Single(notifier.Sent);
    }

    [Fact]
    public async Task FindFile_found_sets_current_path()
    {
        var fs = new InMemoryFileStore();
        fs.Add(Paths.Combine(Ctx.In, "a.pdf"), Pdf);
        var ctx = Ctx.Make();

        Assert.IsType<NextResult>(await new FindFile(fs, new CapturingNotifier()).ExecuteAsync(ctx, default));
        Assert.Equal(Paths.Combine(Ctx.In, "a.pdf"), ctx.State.CurrentPath);
    }

    // ---------- ApplyStamp ----------
    [Fact]
    public async Task ApplyStamp_writes_stamped_to_ToSign_and_removes_original()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Ctx.In, "a.pdf");
        fs.Add(src, Pdf);
        var ctx = Ctx.Make(currentPath: src);

        var res = await new ApplyStamp(fs, new FakeStampRenderer(), new FakeStamper()) { Stamp = "x" }.ExecuteAsync(ctx, default);

        Assert.IsType<NextResult>(res);
        Assert.False(fs.Has(src));
        Assert.Equal(Paths.Combine(Ctx.ToSign, "a.pdf"), ctx.State.CurrentPath);
        Assert.EndsWith("STAMPED", System.Text.Encoding.ASCII.GetString(fs.Get(ctx.State.CurrentPath!)));
    }

    [Fact]
    public async Task ApplyStamp_rejects_non_pdf()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Ctx.In, "a.pdf");
        fs.Add(src, "hello"u8.ToArray());

        var res = await new ApplyStamp(fs, new FakeStampRenderer(), new FakeStamper()) { Stamp = "x" }
            .ExecuteAsync(Ctx.Make(currentPath: src), default);

        Assert.IsType<FailResult>(res);
        Assert.True(fs.Has(src));   // исходник не тронут
    }

    [Fact]
    public async Task ApplyStamp_unknown_stamp_fails_without_touching_files()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Ctx.In, "a.pdf");
        fs.Add(src, Pdf);

        var res = await new ApplyStamp(fs, new FakeStampRenderer(), new FakeStamper()) { Stamp = "missing" }
            .ExecuteAsync(Ctx.Make(currentPath: src), default);

        Assert.IsType<FailResult>(res);
        Assert.True(fs.Has(src));
        Assert.Single(fs.Paths);
    }

    [Fact]
    public async Task ApplyStamp_retry_after_crash_continues_when_stamped_file_already_in_ToSign()
    {
        var fs = new InMemoryFileStore();
        var stamped = Paths.Combine(Ctx.ToSign, "a.pdf");
        fs.Add(stamped, Pdf);   // исходника уже нет (сбой после удаления)
        var ctx = Ctx.Make(currentPath: Paths.Combine(Ctx.In, "a.pdf"));

        var res = await new ApplyStamp(fs, new FakeStampRenderer(), new FakeStamper()) { Stamp = "x" }.ExecuteAsync(ctx, default);

        Assert.IsType<NextResult>(res);
        Assert.Equal(stamped, ctx.State.CurrentPath);
    }

    // ---------- WaitForSignatureFile ----------
    private static (WaitForSignatureFile step, TestTime time, InMemoryFileStore fs, StepContext ctx) SigSetup(TimeSpan? timeout = null)
    {
        var time = new TestTime(new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc));
        var fs = new InMemoryFileStore();
        var step = new WaitForSignatureFile(fs, time) { StableFor = TimeSpan.FromSeconds(30), Timeout = timeout ?? TimeSpan.FromHours(24) };
        var ctx = Ctx.Make(currentPath: Paths.Combine(Ctx.ToSign, "a.pdf"));
        return (step, time, fs, ctx);
    }

    [Fact]
    public async Task Wait_without_pair_waits()
    {
        var (step, time, fs, ctx) = SigSetup();
        fs.Add(Paths.Combine(Ctx.Signed, "a.pdf"), Pdf, time.Now.AddMinutes(-5));   // подписи ещё нет

        Assert.IsType<WaitResult>(await step.ExecuteAsync(ctx, default));
    }

    [Fact]
    public async Task Wait_with_fresh_pair_waits_until_stable()
    {
        var (step, time, fs, ctx) = SigSetup();
        fs.Add(Paths.Combine(Ctx.Signed, "a.pdf"), Pdf, time.Now.AddMinutes(-5));
        fs.Add(Paths.Combine(Ctx.Signed, "a.pdf.sig"), [1], time.Now.AddSeconds(-5));

        Assert.IsType<WaitResult>(await step.ExecuteAsync(ctx, default));
    }

    [Fact]
    public async Task Wait_with_stable_pair_moves_on_and_remembers_sig_path()
    {
        var (step, time, fs, ctx) = SigSetup();
        fs.Add(Paths.Combine(Ctx.Signed, "a.pdf"), Pdf, time.Now.AddMinutes(-5));
        fs.Add(Paths.Combine(Ctx.Signed, "a.pdf.sig"), [1], time.Now.AddMinutes(-5));

        Assert.IsType<NextResult>(await step.ExecuteAsync(ctx, default));
        Assert.Equal(Paths.Combine(Ctx.Signed, "a.pdf"), ctx.State.CurrentPath);
        Assert.Equal(Paths.Combine(Ctx.Signed, "a.pdf.sig"), ctx.State.Extra["sigPath"]);
    }

    [Fact]
    public async Task Wait_times_out_after_24h()
    {
        var (step, time, _, ctx) = SigSetup();

        Assert.IsType<WaitResult>(await step.ExecuteAsync(ctx, default));   // фиксируем время старта
        time.Now = time.Now.AddHours(24).AddMinutes(1);
        Assert.IsType<FailResult>(await step.ExecuteAsync(ctx, default));
    }

    // ---------- ReadSignatureFile / DeleteFile / MoveFile ----------
    [Fact]
    public async Task ReadSignatureFile_reads_neighbour_sig_into_context()
    {
        var fs = new InMemoryFileStore();
        var doc = Paths.Combine(Ctx.Signed, "a.pdf");
        fs.Add(doc, Pdf);
        fs.Add(doc + ".sig", [9, 9]);
        var ctx = Ctx.Make(currentPath: doc);

        Assert.IsType<NextResult>(await new ReadSignatureFile(fs).ExecuteAsync(ctx, default));
        Assert.Equal(Convert.ToBase64String([9, 9]), ctx.State.SignatureBase64);
        Assert.Equal(doc + ".sig", ctx.State.Extra["sigPath"]);
    }

    [Fact]
    public async Task ReadSignatureFile_missing_sig_fails() =>
        Assert.IsType<FailResult>(await new ReadSignatureFile(new InMemoryFileStore())
            .ExecuteAsync(Ctx.Make(currentPath: Paths.Combine(Ctx.Signed, "a.pdf")), default));

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
        Assert.IsType<NextResult>(await step.ExecuteAsync(ctx, default));   // повтор безопасен
    }

    [Fact]
    public async Task MoveFile_adds_suffix_on_collision_and_keeps_original_name()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Ctx.Signed, "a.pdf");
        fs.Add(src, Pdf);
        fs.Add(Paths.Combine(Ctx.Result, "a.pdf"), [0]);   // уже есть в результате — ничего не затираем
        var ctx = Ctx.Make(currentPath: src);

        Assert.IsType<NextResult>(await new MoveFile(fs) { To = Dir.Result }.ExecuteAsync(ctx, default));
        Assert.NotEqual(Paths.Combine(Ctx.Result, "a.pdf"), ctx.State.CurrentPath);
        Assert.Equal([0], fs.Get(Paths.Combine(Ctx.Result, "a.pdf")));
        Assert.False(fs.Has(src));
    }

    [Fact]
    public async Task MoveFile_missing_source_fails() =>
        Assert.IsType<FailResult>(await new MoveFile(new InMemoryFileStore())
            .ExecuteAsync(Ctx.Make(currentPath: Paths.Combine(Ctx.Signed, "a.pdf")), default));
}
