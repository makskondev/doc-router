namespace DocRouter.Engine;

/// <summary>Источник работы: обнаруживает документы и создаёт процессы (view, каталог).</summary>
public interface ISourcePoller
{
    string Name { get; }
    Task PollAsync(CancellationToken ct);
}
