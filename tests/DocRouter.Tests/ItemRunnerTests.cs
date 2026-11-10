using DocRouter.Core;
using DocRouter.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocRouter.Tests;

public sealed class Trace { public List<string> Calls { get; } = new(); }

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
    private sealed record Env(ItemRunner Runner, Trace Trace, CapturingNotifier Notifier);

    private static Env Create(Action<TypeBuilder> steps)
    {
        var b = new DocumentTypesBuilder();
        steps(b.Type("T").FromView("V"));
        var registry = b.Build();

        var trace = new Trace();
        var notifier = new CapturingNotifier();
        var sp = new ServiceCollection().AddSingleton(trace).BuildServiceProvider();
        return new Env(new ItemRunner(sp, registry, notifier, NullLogger<ItemRunner>.Instance), trace, notifier);
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
