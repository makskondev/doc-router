using DocRouter.Core;
using Microsoft.EntityFrameworkCore;

namespace DocRouter.Engine;

public sealed class DocRouterDb(DbContextOptions<DocRouterDb> options) : DbContext(options)
{
    public DbSet<DocumentRun> Runs => Set<DocumentRun>();
    public DbSet<RunStepLog> StepLogs => Set<RunStepLog>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DocumentRun>(e =>
        {
            e.ToTable("runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DocType).HasMaxLength(100);
            e.Property(x => x.SourceKey).HasMaxLength(500);
            e.Property(x => x.ContextJson).HasColumnType("jsonb");
            e.Ignore(x => x.Context);
            // Защита от дублей: активный процесс на (тип, ключ источника) только один.
            // Failed считается активным (не плодим дубли, пока не перезапустят/не отменят).
            e.HasIndex(x => new { x.DocType, x.SourceKey }).IsUnique()
                .HasFilter("status NOT IN ('Completed','Cancelled')");
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });

        b.Entity<RunStepLog>(e =>
        {
            e.ToTable("run_steps");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RunId);
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.Status, x.Id });
        });
    }
}
