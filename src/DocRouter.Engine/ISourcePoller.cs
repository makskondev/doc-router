using DocRouter.Core;

namespace DocRouter.Engine;

/// <summary>Источник работы: на каждом опросе возвращает документы, которые сейчас «в очереди» (view, каталог).</summary>
public interface ISourcePoller
{
    string Name { get; }
    Task<IReadOnlyList<WorkItem>> DiscoverAsync(CancellationToken ct);
}
