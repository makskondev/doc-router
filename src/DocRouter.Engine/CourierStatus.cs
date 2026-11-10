namespace DocRouter.Engine;

/// <summary>Для /health/ready: жив ли цикл опроса.</summary>
public sealed class CourierStatus(TimeProvider time)
{
    public DateTime StartedUtc { get; } = time.GetUtcNow().UtcDateTime;
    public DateTime? LastCycleUtc { get; private set; }
    public void MarkCycle(DateTime utcNow) => LastCycleUtc = utcNow;
}
