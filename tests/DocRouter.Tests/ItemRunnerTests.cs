using DocRouter.Core;
using DocRouter.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocRouter.Tests;

public sealed class Trace
{
    public List<string> Calls { get; } = new();
    public bool GateOpen;
    public bool BreakB;
}

public sealed class OkStep(Trace t) : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct) { t.Calls.Add("ok"); return Task.FromResult(StepResult.Next()); }
}
public sealed class WaitingStep(Trace t) : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct) { t.Calls.Add("wait"); return Task.FromResult(StepResult.Wait("sign pending")); }
}
public sealed class FailingStep(Trace t) : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct) { t.Calls.Add("fail"); return Task.FromResult(StepResult.Fail("bad signature")); }
}
public sealed class ThrowingStep(Trace t) : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct) { t.Calls.Add("throw"); throw new IOException("smb down"); }
}
public sealed class LastStep(Trace t) : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct) { t.Calls.Add("last"); return Task.FromResult(StepResult.Next()); }
}

public class ItemRunnerTests
{
    private sealed record Env(ItemRunner Runner, Trace Trace, CapturingNotifier Notifier, InMemoryStateStore States, RecordingJournal Journal);

    private static Env Create(Action<TypeBuilder> steps)
    {
        var b = new DocumentTypesBuilder();
        steps(b.Type("T").FromView("V"));
        var registry = b.Build();

        var trace = new Trace();
        var notifier = new CapturingNotifier();
        var sp = new ServiceCollection().AddSingleton(trace).BuildServiceProvider();
        var states = new InMemoryStateStore();
        var journal = new RecordingJournal();
        return new Env(new ItemRunner(sp, registry, notifier, states, journal, NullLogger<ItemRunner>.Instance), trace, notifier, states, journal);
    }

    [Fact]
    public async Task All_steps_ok_runs_to_the_end_without_emails()
    {
        var env = Create(t => t.Step<OkStep>().Step<LastStep>());
        await env.Runner.RunAsync(Ctx.Item(), default);

        Assert.Equal(new[] { "ok", "last" }, env.Trace.Calls);
        Assert.Empty(env.Notifier.Failures);
    }

    [Fact]
    public async Task Wait_stops_quietly_and_skips_later_steps()
    {
        var env = Create(t => t.Step<OkStep>().Step<WaitingStep>().Step<LastStep>());
        await env.Runner.RunAsync(Ctx.Item(), default);

        Assert.Equal(new[] { "ok", "wait" }, env.Trace.Calls);
        Assert.Empty(env.Notifier.Failures);
    }

    [Fact]
    public async Task Fail_sends_email_with_step_and_reason_and_stops()
    {
        var env = Create(t => t.Step<OkStep>().Step<FailingStep>().Step<LastStep>());
        await env.Runner.RunAsync(Ctx.Item(), default);

        Assert.Equal(new[] { "ok", "fail" }, env.Trace.Calls);
        var f = Assert.Single(env.Notifier.Failures);
        Assert.Equal("FailingStep", f.Step);
        Assert.Equal("bad signature", f.Reason);
        Assert.Equal("42", f.Item.ExternalId);
    }

    [Fact]
    public async Task Exception_is_treated_as_failure_with_email()
    {
        var env = Create(t => t.Step<ThrowingStep>().Step<LastStep>());
        await env.Runner.RunAsync(Ctx.Item(), default);

        Assert.Equal(new[] { "throw" }, env.Trace.Calls);
        Assert.Contains("smb down", Assert.Single(env.Notifier.Failures).Reason);
    }

    [Fact]
    public async Task Every_failed_poll_sends_another_email_and_restarts_from_first_step()
    {
        var env = Create(t => t.Step<OkStep>().Step<FailingStep>());

        await env.Runner.RunAsync(Ctx.Item(), default);
        await env.Runner.RunAsync(Ctx.Item(), default);

        Assert.Equal(2, env.Notifier.Failures.Count);
        Assert.Equal(new[] { "ok", "fail", "ok", "fail" }, env.Trace.Calls);
    }

    [Fact]
    public async Task Unknown_type_sends_email_and_runs_nothing()
    {
        var env = Create(t => t.Step<OkStep>());
        await env.Runner.RunAsync(Ctx.Item(docType: "NOT_DESCRIBED"), default);

        Assert.Empty(env.Trace.Calls);
        Assert.Single(env.Notifier.Failures);
    }
}

// ---- шаги для проверки состояния ----
public sealed class StepA(Trace t) : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        t.Calls.Add("A");
        ctx.State.Extra["a"] = "done-by-A";
        ctx.State.ClientCode = "C-1";
        ctx.State.CurrentPath = @"\\srv\x\a.pdf";
        ctx.State.Content = [1, 2, 3];                       // содержимое не должно попасть в сохранённое состояние
        return Task.FromResult(StepResult.Next());
    }
}
public sealed class StepB(Trace t) : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        t.Calls.Add("B");
        return Task.FromResult(t.BreakB ? StepResult.Fail("B broke") : StepResult.Next());
    }
}
public sealed class GateStep(Trace t) : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        t.Calls.Add("Gate");
        if (!t.GateOpen) return Task.FromResult(StepResult.Wait("ждём человека"));
        ctx.State.Extra["gate"] = "opened";
        return Task.FromResult(StepResult.Next());
    }
}
public sealed class StepC(Trace t) : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        t.Calls.Add("C:" + ctx.State.Extra.GetValueOrDefault("a") + ":" + ctx.State.Extra.GetValueOrDefault("gate") + ":" + ctx.State.CurrentPath);
        return Task.FromResult(StepResult.Next());
    }
}

public class StatefulRunnerTests
{
    private sealed record Env(ItemRunner Runner, Trace Trace, CapturingNotifier Notifier, InMemoryStateStore States, RecordingJournal Journal);

    private static Env Create(bool stateful = true, Action<TypeBuilder>? steps = null)
    {
        var b = new DocumentTypesBuilder();
        var t = b.Type("T").FromView("V");
        if (stateful) t.Stateful();
        (steps ?? (x => x.Step<StepA>().Step<StepB>().Step<GateStep>().Step<StepC>()))(t);
        var registry = b.Build();

        var trace = new Trace();
        var notifier = new CapturingNotifier();
        var states = new InMemoryStateStore();
        var journal = new RecordingJournal();
        var sp = new ServiceCollection().AddSingleton(trace).BuildServiceProvider();
        return new Env(new ItemRunner(sp, registry, notifier, states, journal, NullLogger<ItemRunner>.Instance), trace, notifier, states, journal);
    }

    private static WorkItem Item() => Ctx.Item(docType: "T", fileName: "a.pdf", id: "42", kind: "заявление");

    [Fact]
    public async Task Done_steps_are_skipped_on_the_next_poll_and_data_survives_between_polls()
    {
        var env = Create();

        await env.Runner.RunAsync(Item(), default);                    // A, B выполнены, Gate ждёт
        Assert.Equal(new[] { "A", "B", "Gate" }, env.Trace.Calls);
        var saved = env.States.Rows[("T", "42")];
        Assert.Equal(new[] { "StepA", "StepB" }, saved.Done);
        Assert.Equal("done-by-A", saved.State.Extra["a"]);

        env.Trace.GateOpen = true;
        await env.Runner.RunAsync(Item(), default);                    // A и B НЕ повторяются, данные A видны в C
        Assert.Equal(new[] { "A", "B", "Gate", "Gate", @"C:done-by-A:opened:\\srv\x\a.pdf" }, env.Trace.Calls);
        Assert.NotNull(env.States.Rows[("T", "42")].Completed);        // цепочка завершена
        Assert.Empty(env.Notifier.Failures);
    }

    [Fact]
    public async Task File_content_is_never_persisted()
    {
        var env = Create();
        await env.Runner.RunAsync(Item(), default);
        Assert.Null(env.States.Rows[("T", "42")].State.Content);
    }

    [Fact]
    public async Task Failure_resumes_from_the_failed_step_not_from_the_beginning()
    {
        var env = Create();
        env.Trace.BreakB = true;

        await env.Runner.RunAsync(Item(), default);
        Assert.Equal(new[] { "A", "B" }, env.Trace.Calls);
        Assert.Equal(new[] { "StepA" }, env.States.Rows[("T", "42")].Done);     // сохранено всё, что удалось
        Assert.Single(env.Notifier.Failures);

        env.Trace.BreakB = false;
        env.Trace.GateOpen = true;
        await env.Runner.RunAsync(Item(), default);
        Assert.Equal(new[] { "A", "B", "B", "Gate" }, env.Trace.Calls.Take(4));   // A не повторён
    }

    [Fact]
    public async Task Waiting_state_is_saved_so_data_set_by_a_waiting_step_is_not_lost()
    {
        var env = Create(steps: t => t.Step<WaitWithMemoryStep>().Step<StepC>());
        await env.Runner.RunAsync(Item(), default);
        Assert.Equal("first-seen", env.States.Rows[("T", "42")].State.Extra["marker"]);
    }

    [Fact]
    public async Task State_save_failure_is_reported_and_the_step_is_repeated_next_poll()
    {
        var env = Create();
        env.States.ThrowOnSave = true;

        await env.Runner.RunAsync(Item(), default);
        var f = Assert.Single(env.Notifier.Failures);
        Assert.Contains("сохранить состояние", f.Reason);
        Assert.Equal(new[] { "A" }, env.Trace.Calls);                  // дальше не пошли

        env.States.ThrowOnSave = false;
        await env.Runner.RunAsync(Item(), default);
        Assert.Equal("A", env.Trace.Calls[1]);                         // шаг повторён (шаги идемпотентны)
    }

    [Fact]
    public async Task State_load_failure_stops_before_any_step_and_reports()
    {
        var env = Create();
        env.States.ThrowOnLoad = true;

        await env.Runner.RunAsync(Item(), default);
        Assert.Empty(env.Trace.Calls);
        Assert.Contains("загрузить состояние", Assert.Single(env.Notifier.Failures).Reason);
    }

    [Fact]
    public async Task Completed_state_with_the_record_visible_again_means_reprocessing_from_scratch()
    {
        var env = Create();
        env.Trace.GateOpen = true;
        await env.Runner.RunAsync(Item(), default);                    // полный проход, состояние завершено
        env.Trace.Calls.Clear();

        await env.Runner.RunAsync(Item(), default);                    // запись снова во вью → всё заново
        Assert.Equal(new[] { "A", "B", "Gate" }, env.Trace.Calls.Take(3));
    }

    [Fact]
    public async Task All_steps_done_but_not_marked_completed_and_record_still_visible_restarts_from_scratch()
    {
        var env = Create();
        env.Trace.GateOpen = true;
        await env.Runner.RunAsync(Item(), default);
        // имитируем сбой отметки «завершён»: все шаги выполнены, а completed_at не стоит
        var row = env.States.Rows[("T", "42")];
        env.States.Rows[("T", "42")] = (row.State, row.Done, null);
        env.Trace.Calls.Clear();

        await env.Runner.RunAsync(Item(), default);
        Assert.Equal("A", env.Trace.Calls.First());
    }

    [Fact]
    public async Task Stateless_types_never_touch_the_state_store()
    {
        var env = Create(stateful: false);
        env.Trace.GateOpen = true;
        await env.Runner.RunAsync(Item(), default);

        Assert.Equal(0, env.States.Saves);
        Assert.Empty(env.States.Rows);
        Assert.Equal(new[] { "A", "B", "Gate" }, env.Trace.Calls.Take(3));
    }

    [Fact]
    public async Task Two_documents_have_independent_state()
    {
        var env = Create();
        await env.Runner.RunAsync(Ctx.Item(docType: "T", fileName: "a.pdf", id: "1"), default);
        env.Trace.GateOpen = true;
        await env.Runner.RunAsync(Ctx.Item(docType: "T", fileName: "b.pdf", id: "2"), default);

        Assert.NotNull(env.States.Rows[("T", "2")].Completed);
        Assert.Null(env.States.Rows[("T", "1")].Completed);            // первый всё ещё ждёт
    }

    // ---------- журнал операций ----------
    [Fact]
    public async Task Journal_records_waiting_then_completed_with_client_and_final_path()
    {
        var env = Create();
        await env.Runner.RunAsync(Item(), default);
        env.Trace.GateOpen = true;
        await env.Runner.RunAsync(Item(), default);

        Assert.Equal(new[] { OperationOutcome.Waiting, OperationOutcome.Completed }, env.Journal.Records.Select(r => r.Outcome));
        var waiting = env.Journal.Records[0];
        Assert.Equal(("T", "42", "a.pdf", "заявление", "GateStep"), (waiting.DocType, waiting.SourceKey, waiting.FileName, waiting.Kind, waiting.Step));
        var done = env.Journal.Records[1];
        Assert.Equal("C-1", done.ClientCode);
        Assert.Equal(@"\\srv\x\a.pdf", done.TargetPath);
    }

    [Fact]
    public async Task Journal_records_failures_with_step_and_reason_for_stateless_types_too()
    {
        var env = Create(stateful: false, steps: t => t.Step<StepA>().Step<StepB>());
        env.Trace.BreakB = true;
        await env.Runner.RunAsync(Item(), default);

        var r = Assert.Single(env.Journal.Records);
        Assert.Equal((OperationOutcome.Failed, "StepB", "B broke"), (r.Outcome, r.Step, r.Message));
    }

    [Fact]
    public async Task Journal_failure_never_breaks_processing_or_adds_failure_emails()
    {
        var env = Create(stateful: false, steps: t => t.Step<StepA>().Step<StepC>());
        env.Journal.Throw = true;

        await env.Runner.RunAsync(Item(), default);
        Assert.Equal(2, env.Trace.Calls.Count);                        // цепочка прошла до конца
        Assert.Empty(env.Notifier.Failures);
    }
}

public sealed class WaitWithMemoryStep : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct)
    {
        ctx.State.Extra["marker"] = "first-seen";
        return Task.FromResult(StepResult.Wait("ждём"));
    }
}
