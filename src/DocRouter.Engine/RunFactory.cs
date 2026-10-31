using DocRouter.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DocRouter.Engine;

/// <summary>Создание процессов с защитой от дублей (повторный опрос view/каталога).</summary>
public sealed class RunFactory(IServiceScopeFactory scopes, RouteRegistry routes)
{
    public async Task<bool> TryCreateAsync(
        string docType, SourceKind source, string sourceKey, string fileName,
        IReadOnlyDictionary<string, string> extra, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocRouterDb>();

        var exists = await db.Runs.AnyAsync(r => r.DocType == docType && r.SourceKey == sourceKey
            && r.Status != RunStatus.Completed && r.Status != RunStatus.Cancelled, ct);
        if (exists) return false;

        var run = new DocumentRun
        {
            DocType = docType,
            Source = source,
            SourceKey = sourceKey,
            StepName = routes.Get(docType).Steps[0].Name
        };
        run.Context.FileName = fileName;
        foreach (var (k, v) in extra) run.Context.Extra[k] = v;
        run.FlushContext();

        db.Runs.Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return false; // параллельный под успел создать
        }
    }
}
