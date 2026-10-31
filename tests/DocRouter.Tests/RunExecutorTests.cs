using DocRouter.Core;
using DocRouter.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocRouter.Tests;

public sealed class OkStep : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct) => Task.FromResult(StepResult.Next());
}
public sealed class WaitingStep : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct) => Task.FromResult(StepResult.Wait(TimeSpan.FromMinutes(10)));
}
public sealed class TransientStep : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct) => Task.FromResult(StepResult.Transient("smb down"));
}
public sealed class FailingStep : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct) => Task.FromResult(StepResult.Fail("bad signature"));
}
public sealed class ThrowingStep : IStep
{
    public Task<StepResult> ExecuteAsync(StepContext ctx, CancellationToken ct) => throw new IOException("boom");
}

public class RunExecutorTests
{
    private sealed class Env
    {
        public required ServiceProvider Sp { get; init; }
        public required RunExecutor Executor { get; init; }

        public async Task<Guid> AddRunAsync(string firstStep, int stepIndex = 0)
        {
            await using var scope = Sp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DocRouterDb>();
            var run = new DocumentRun { DocType = "T", SourceKey = Guid.NewGuid().ToString(), StepName = firstStep, StepIndex = stepIndex };
            run.Context.FileName = "a.pdf";
            run.FlushContext();
            db.Runs.Add(run);
            await db.SaveChangesAsync();
            return run.Id;
        }

        public async Task<(DocumentRun run, int steps, List<Notification> mails)> LoadAsync(Guid id)
        {
            await using var scope = Sp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DocRouterDb>();
            return (await db.Runs.AsNoTracking().FirstAsync(r => r.Id == id),
                    await db.StepLogs.CountAsync(l => l.RunId == id),
                    await db.Notifications.AsNoTracking().ToListAsync());
        }
    }

    private static Env Create(Action<StepListBuilder> route, int maxTransient = 2)
    {
        var b = new RoutesBuilder();
        route(b.For("T"));
        var routes = b.Build();

        var types = new DocumentTypesOptions
        {
            ["T"] = new DocumentTypeOptions { Incoming = Ctx.In, Result = Ctx.Result, ErrorRecipients = ["team@example.com"] }
        };
        var engine = new EngineOptions { MaxTransientAttempts = maxTransient, BackoffMinutes = [1, 5] };

        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<DocRouterDb>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(Options.Create(types));
        services.AddSingleton(Options.Create(engine));
        services.AddSingleton<ILogger<DbNotifier>>(NullLogger<DbNotifier>.Instance);
        services.AddScoped<INotifier, DbNotifier>();
        var sp = services.BuildServiceProvider();

        return new Env
        {
            Sp = sp,
            Executor = new RunExecutor(sp.GetRequiredService<IServiceScopeFactory>(), routes,
                Options.Create(engine), Options.Create(types), NullLogger<RunExecutor>.Instance)
        };
    }

    [Fact]
    public async Task All_steps_ok_completes_and_logs_each_step()
    {
        var env = Create(r => r.Step<OkStep>().Step<OkStep>());
        var id = await env.AddRunAsync("OkStep");

        await env.Executor.ExecuteAsync(id, default);
        var (run, steps, _) = await env.LoadAsync(id);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.NotNull(run.CompletedAt);
        Assert.Equal(2, steps);
        Assert.Null(run.LockedBy);
    }

    [Fact]
    public async Task Wait_stops_run_in_waiting_state_at_same_step()
    {
        var env = Create(r => r.Step<OkStep>().Step<WaitingStep>().Step<OkStep>());
        var id = await env.AddRunAsync("OkStep");

        await env.Executor.ExecuteAsync(id, default);
        var (run, _, _) = await env.LoadAsync(id);

        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal("WaitingStep", run.StepName);
        Assert.Equal(1, run.StepIndex);
        Assert.True(run.NextAttemptAt > DateTime.UtcNow.AddMinutes(9));
    }

    [Fact]
    public async Task Transient_retries_with_backoff_then_fails_with_one_email()
    {
        var env = Create(r => r.Step<TransientStep>(), maxTransient: 2);
        var id = await env.AddRunAsync("TransientStep");

        await env.Executor.ExecuteAsync(id, default);
        var (r1, _, m1) = await env.LoadAsync(id);
        Assert.Equal(RunStatus.Pending, r1.Status);
        Assert.Equal(1, r1.Attempt);
        Assert.Empty(m1);

        await env.Executor.ExecuteAsync(id, default);
        await env.Executor.ExecuteAsync(id, default);   // 3-я попытка превышает лимит 2
        var (r3, _, m3) = await env.LoadAsync(id);

        Assert.Equal(RunStatus.Failed, r3.Status);
        Assert.Single(m3);
        Assert.Contains("team@example.com", m3[0].Recipients);
    }

    [Fact]
    public async Task Fail_marks_failed_and_enqueues_email_with_reason()
    {
        var env = Create(r => r.Step<FailingStep>());
        var id = await env.AddRunAsync("FailingStep");

        await env.Executor.ExecuteAsync(id, default);
        var (run, _, mails) = await env.LoadAsync(id);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("bad signature", run.Error);
        Assert.Single(mails);
        Assert.Contains("bad signature", mails[0].Body);
    }

    [Fact]
    public async Task Unexpected_exception_is_treated_as_transient()
    {
        var env = Create(r => r.Step<ThrowingStep>());
        var id = await env.AddRunAsync("ThrowingStep");

        await env.Executor.ExecuteAsync(id, default);
        var (run, _, mails) = await env.LoadAsync(id);

        Assert.Equal(RunStatus.Pending, run.Status);
        Assert.Equal(1, run.Attempt);
        Assert.Empty(mails);
    }

    [Fact]
    public async Task Step_is_resolved_by_name_when_route_changed()
    {
        var env = Create(r => r.Step<OkStep>().Step<OkStep>());   // OkStep, OkStep#2
        var id = await env.AddRunAsync("OkStep#2", stepIndex: 0);  // индекс устарел, имя верное

        await env.Executor.ExecuteAsync(id, default);
        var (run, steps, _) = await env.LoadAsync(id);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(1, steps);   // выполнен только второй шаг
    }

    [Fact]
    public async Task Missing_step_name_fails_the_run()
    {
        var env = Create(r => r.Step<OkStep>());
        var id = await env.AddRunAsync("RemovedStep");

        await env.Executor.ExecuteAsync(id, default);
        var (run, _, mails) = await env.LoadAsync(id);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Single(mails);
    }
}
