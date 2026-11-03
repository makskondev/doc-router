using DocRouter.Core;
using DocRouter.Steps;

namespace DocRouter.Tests;

public class StepTests
{
    private static readonly byte[] Pdf = "%PDF-1.7 test"u8.ToArray();

    // ---------- FindFile ----------
    [Fact]
    public async Task FindFile_finds_in_incoming()
    {
        var fs = new InMemoryFileStore();
        fs.Add(Paths.Combine(Ctx.In, "a.pdf"), Pdf);
        var ctx = Ctx.Make();

        Assert.IsType<NextResult>(await new FindFile(fs).ExecuteAsync(ctx, default));
        Assert.Equal(Dir.Incoming, ctx.State.Location);
        Assert.Equal(Paths.Combine(Ctx.In, "a.pdf"), ctx.State.CurrentPath);
    }

    [Fact]
    public async Task FindFile_prefers_most_advanced_location()
    {
        var fs = new InMemoryFileStore();
        fs.Add(Paths.Combine(Ctx.In, "a.pdf"), Pdf);        // «хвост» от прошлого опроса
        fs.Add(Paths.Combine(Ctx.ToSign, "a.pdf"), Pdf);
        var ctx = Ctx.Make();
        await new FindFile(fs).ExecuteAsync(ctx, default);
        Assert.Equal(Dir.ToSign, ctx.State.Location);

        fs.Add(Paths.Combine(Ctx.Signed, "a.pdf"), Pdf);
        var ctx2 = Ctx.Make();
        await new FindFile(fs).ExecuteAsync(ctx2, default);
        Assert.Equal(Dir.Signed, ctx2.State.Location);
    }

    [Fact]
    public async Task FindFile_does_not_look_in_result()
    {
        var fs = new InMemoryFileStore();
        fs.Add(Paths.Combine(Ctx.Result, "a.pdf"), Pdf);   // старый документ с тем же именем в архиве
        Assert.IsType<FailResult>(await new FindFile(fs).ExecuteAsync(Ctx.Make(), default));
    }

    [Fact]
    public async Task FindFile_skips_unconfigured_folders()
    {
        var fs = new InMemoryFileStore();
        fs.Add(Paths.Combine(Ctx.In, "a.pdf"), Pdf);
        var route = new RouteOptions { Incoming = Ctx.In, Result = Ctx.Result };   // маршрут без ToSign/Signed
        var ctx = Ctx.Make(route: route);

        Assert.IsType<NextResult>(await new FindFile(fs).ExecuteAsync(ctx, default));
        Assert.Equal(Dir.Incoming, ctx.State.Location);
    }

    [Fact]
    public async Task FindFile_not_found_fails() =>
        Assert.IsType<FailResult>(await new FindFile(new InMemoryFileStore()).ExecuteAsync(Ctx.Make(), default));

    // ---------- ApplyStamp ----------
    private static ApplyStamp Stamp(InMemoryFileStore fs, FakeStampRenderer? r = null) =>
        new(fs, r ?? new FakeStampRenderer(), new FakeStamper());

    [Fact]
    public async Task ApplyStamp_writes_stamped_to_ToSign_same_name_and_removes_original()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Ctx.In, "a.pdf");
        fs.Add(src, Pdf);
        var ctx = Ctx.Make(location: Dir.Incoming, currentPath: src);

        Assert.IsType<NextResult>(await Stamp(fs).ExecuteAsync(ctx, default));

        Assert.False(fs.Has(src));
        Assert.Equal(Paths.Combine(Ctx.ToSign, "a.pdf"), ctx.State.CurrentPath);
        Assert.Equal(Dir.ToSign, ctx.State.Location);
        Assert.EndsWith("STAMPED", System.Text.Encoding.ASCII.GetString(fs.Get(ctx.State.CurrentPath!)));
    }

    [Fact]
    public async Task ApplyStamp_name_priority_step_then_type_then_doctype()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Ctx.In, "a.pdf");

        fs.Add(src, Pdf);
        var r1 = new FakeStampRenderer();
        await Stamp(fs, r1).ExecuteAsync(Ctx.Make(location: Dir.Incoming, currentPath: src,
            type: new DocTypeOptions { Stamp = "fromType" }, docType: "Contract"), default);
        Assert.Equal("fromType", r1.LastStampName);

        fs = new InMemoryFileStore(); fs.Add(src, Pdf);
        var r2 = new FakeStampRenderer();
        await Stamp(fs, r2).ExecuteAsync(Ctx.Make(location: Dir.Incoming, currentPath: src, docType: "Contract"), default);
        Assert.Equal("Contract", r2.LastStampName);
    }

    [Fact]
    public async Task ApplyStamp_without_type_and_stamp_fails_and_keeps_file()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Ctx.In, "a.pdf");
        fs.Add(src, Pdf);

        var res = await Stamp(fs).ExecuteAsync(Ctx.Make(location: Dir.Incoming, currentPath: src, docType: ""), default);

        Assert.IsType<FailResult>(res);
        Assert.True(fs.Has(src));
    }

    [Fact]
    public async Task ApplyStamp_rejects_non_pdf()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Ctx.In, "a.pdf");
        fs.Add(src, "hello"u8.ToArray());

        Assert.IsType<FailResult>(await Stamp(fs).ExecuteAsync(Ctx.Make(location: Dir.Incoming, currentPath: src), default));
        Assert.True(fs.Has(src));
    }

    [Fact]
    public async Task ApplyStamp_unknown_stamp_fails_without_touching_files()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Ctx.In, "a.pdf");
        fs.Add(src, Pdf);

        var res = await Stamp(fs).ExecuteAsync(Ctx.Make(location: Dir.Incoming, currentPath: src,
            type: new DocTypeOptions { Stamp = "missing" }), default);

        Assert.IsType<FailResult>(res);
        Assert.Single(fs.AllPaths);
    }

    [Fact]
    public async Task ApplyStamp_does_not_overwrite_existing_file_in_ToSign()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Ctx.In, "a.pdf");
        var existing = Paths.Combine(Ctx.ToSign, "a.pdf");
        fs.Add(src, Pdf);
        fs.Add(existing, [7]);

        Assert.IsType<FailResult>(await Stamp(fs).ExecuteAsync(Ctx.Make(location: Dir.Incoming, currentPath: src), default));
        Assert.Equal(new byte[] { 7 }, fs.Get(existing));
        Assert.True(fs.Has(src));
    }

    [Fact]
    public async Task ApplyStamp_skips_when_already_in_ToSign_and_cleans_leftover_original()
    {
        var fs = new InMemoryFileStore();
        var stamped = Paths.Combine(Ctx.ToSign, "a.pdf");
        var leftover = Paths.Combine(Ctx.In, "a.pdf");
        fs.Add(stamped, Pdf);
        fs.Add(leftover, Pdf);   // сбой на прошлом опросе между записью и удалением

        Assert.IsType<NextResult>(await Stamp(fs).ExecuteAsync(Ctx.Make(location: Dir.ToSign, currentPath: stamped), default));
        Assert.False(fs.Has(leftover));
        Assert.True(fs.Has(stamped));
    }

    [Fact]
    public async Task ApplyStamp_skips_when_already_signed()
    {
        var fs = new InMemoryFileStore();
        var signed = Paths.Combine(Ctx.Signed, "a.pdf");
        fs.Add(signed, Pdf);

        Assert.IsType<NextResult>(await Stamp(fs).ExecuteAsync(Ctx.Make(location: Dir.Signed, currentPath: signed), default));
        Assert.Single(fs.AllPaths);
    }

    // ---------- WaitForSignatureFile ----------
    private static (WaitForSignatureFile step, TestTime time, InMemoryFileStore fs) WaitSetup()
    {
        var time = new TestTime(new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc));
        var fs = new InMemoryFileStore();
        return (new WaitForSignatureFile(fs, time) { StableFor = TimeSpan.FromSeconds(30), Timeout = TimeSpan.FromHours(24) }, time, fs);
    }

    [Fact]
    public async Task Wait_in_ToSign_waits_quietly()
    {
        var (step, time, fs) = WaitSetup();
        var p = Paths.Combine(Ctx.ToSign, "a.pdf");
        fs.Add(p, Pdf, time.Now.AddHours(-3));

        Assert.IsType<WaitResult>(await step.ExecuteAsync(Ctx.Make(location: Dir.ToSign, currentPath: p), default));
    }

    [Fact]
    public async Task Wait_in_ToSign_fails_after_24h_by_file_mtime()
    {
        var (step, time, fs) = WaitSetup();
        var p = Paths.Combine(Ctx.ToSign, "a.pdf");
        fs.Add(p, Pdf, time.Now.AddHours(-25));

        Assert.IsType<FailResult>(await step.ExecuteAsync(Ctx.Make(location: Dir.ToSign, currentPath: p), default));
    }

    [Fact]
    public async Task Wait_in_Signed_with_stable_pair_moves_on_and_remembers_sig_path()
    {
        var (step, time, fs) = WaitSetup();
        var doc = Paths.Combine(Ctx.Signed, "a.pdf");
        fs.Add(doc, Pdf, time.Now.AddMinutes(-5));
        fs.Add(doc + ".sig", [1], time.Now.AddMinutes(-5));
        var ctx = Ctx.Make(location: Dir.Signed, currentPath: doc);

        Assert.IsType<NextResult>(await step.ExecuteAsync(ctx, default));
        Assert.Equal(doc, ctx.State.CurrentPath);
        Assert.Equal(doc + ".sig", ctx.State.Extra["sigPath"]);
    }

    [Fact]
    public async Task Wait_in_Signed_without_sig_or_with_fresh_sig_waits()
    {
        var (step, time, fs) = WaitSetup();
        var doc = Paths.Combine(Ctx.Signed, "a.pdf");
        fs.Add(doc, Pdf, time.Now.AddMinutes(-5));
        Assert.IsType<WaitResult>(await step.ExecuteAsync(Ctx.Make(location: Dir.Signed, currentPath: doc), default));

        fs.Add(doc + ".sig", [1], time.Now.AddSeconds(-5));
        Assert.IsType<WaitResult>(await step.ExecuteAsync(Ctx.Make(location: Dir.Signed, currentPath: doc), default));
    }

    [Fact]
    public async Task Wait_in_unexpected_location_fails()
    {
        var (step, _, _) = WaitSetup();
        Assert.IsType<FailResult>(await step.ExecuteAsync(Ctx.Make(location: Dir.Incoming), default));
    }

    // ---------- ReadSignatureFile / DeleteFile / MoveFile ----------
    [Fact]
    public async Task ReadSignatureFile_reads_neighbour_sig_into_context()
    {
        var fs = new InMemoryFileStore();
        var doc = Paths.Combine(Ctx.Signed, "a.pdf");
        fs.Add(doc, Pdf);
        fs.Add(doc + ".sig", [9, 9]);
        var ctx = Ctx.Make(location: Dir.Signed, currentPath: doc);

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
        Assert.IsType<NextResult>(await step.ExecuteAsync(ctx, default));
    }

    [Fact]
    public async Task MoveFile_adds_suffix_on_collision_and_never_overwrites()
    {
        var fs = new InMemoryFileStore();
        var src = Paths.Combine(Ctx.Signed, "a.pdf");
        fs.Add(src, Pdf);
        fs.Add(Paths.Combine(Ctx.Result, "a.pdf"), [0]);
        var ctx = Ctx.Make(currentPath: src);

        Assert.IsType<NextResult>(await new MoveFile(fs) { To = Dir.Result }.ExecuteAsync(ctx, default));
        Assert.NotEqual(Paths.Combine(Ctx.Result, "a.pdf"), ctx.State.CurrentPath);
        Assert.Equal(new byte[] { 0 }, fs.Get(Paths.Combine(Ctx.Result, "a.pdf")));
        Assert.False(fs.Has(src));
    }

    [Fact]
    public async Task MoveFile_missing_source_fails() =>
        Assert.IsType<FailResult>(await new MoveFile(new InMemoryFileStore())
            .ExecuteAsync(Ctx.Make(currentPath: Paths.Combine(Ctx.Signed, "a.pdf")), default));
}
