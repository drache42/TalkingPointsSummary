using Microsoft.EntityFrameworkCore;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Data;

/// <summary>
/// Entity Framework database context for the TalkingPoints summary domain.
/// </summary>
public class AppDbContext : DbContext
{
    /// <summary>
    /// Initializes a new database context instance.
    /// </summary>
    /// <param name="options">Configured Entity Framework options for the context.</param>
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    /// <summary>
    /// Parent records managed by the application.
    /// </summary>
    public DbSet<Parent> Parents => Set<Parent>();

    /// <summary>
    /// Child records associated with parents.
    /// </summary>
    public DbSet<Child> Children => Set<Child>();

    /// <summary>
    /// Messages fetched from TalkingPoints.
    /// </summary>
    public DbSet<Message> Messages => Set<Message>();

    /// <summary>
    /// News items extracted from messages or newsletters.
    /// </summary>
    public DbSet<NewsItem> NewsItems => Set<NewsItem>();

    /// <summary>
    /// Generated summary emails stored for historical reference.
    /// </summary>
    public DbSet<Summary> Summaries => Set<Summary>();

    /// <summary>
    /// Recorded pipeline execution attempts and outcomes.
    /// </summary>
    public DbSet<PipelineRun> PipelineRuns => Set<PipelineRun>();

    /// <summary>
    /// Configures entity mappings, constraints, indexes, and relationships.
    /// </summary>
    /// <param name="modelBuilder">Builder used to define the EF model.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Parent
        modelBuilder.Entity<Parent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.TalkingPointsToken).IsRequired();
            entity.Property(e => e.TalkingPointsContactId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.EmailRecipients).IsRequired();
            entity.HasMany(e => e.Children).WithOne(c => c.Parent).HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.Messages).WithOne(m => m.Parent).HasForeignKey(m => m.ParentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.NewsItems).WithOne(n => n.Parent).HasForeignKey(n => n.ParentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.Summaries).WithOne(s => s.Parent).HasForeignKey(s => s.ParentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany<PipelineRun>().WithOne(run => run.Parent).HasForeignKey(run => run.ParentId).OnDelete(DeleteBehavior.SetNull);
        });

        // Child
        modelBuilder.Entity<Child>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.School).IsRequired().HasMaxLength(300);
            entity.Property(e => e.Emoji).HasMaxLength(10).HasDefaultValue("📚");
        });

        // Message
        modelBuilder.Entity<Message>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ExternalMessageId).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => new { e.ParentId, e.ExternalMessageId }).IsUnique();
            entity.Property(e => e.ContactMessageId).HasMaxLength(100);
            entity.Property(e => e.StudentName).HasMaxLength(200);
            entity.Property(e => e.FromName).HasMaxLength(200);
            entity.HasIndex(e => new { e.ParentId, e.ProcessedAt });
        });

        // NewsItem
        modelBuilder.Entity<NewsItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceMessageId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.SourceType).IsRequired().HasConversion<string>().HasMaxLength(50);
            entity.HasIndex(e => new { e.ParentId, e.SourceMessageId, e.SourceType }).IsUnique();
            entity.Property(e => e.NewsletterUrl).HasMaxLength(2000);
            entity.Property(e => e.FromName).HasMaxLength(200);
            entity.Property(e => e.StudentName).HasMaxLength(200);
            entity.HasIndex(e => new { e.ParentId, e.CreatedAt });
        });

        // Summary
        modelBuilder.Entity<Summary>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ParentId, e.CreatedAt });
        });

        // PipelineRun
        modelBuilder.Entity<PipelineRun>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Trigger).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Status).IsRequired().HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Error).HasMaxLength(1000);
            entity.HasIndex(e => e.StartedAt);
            entity.HasIndex(e => new { e.Trigger, e.ScheduledDate })
                .IsUnique()
                .HasFilter("\"Trigger\" = 'schedule' AND \"ScheduledDate\" IS NOT NULL");
        });
    }
}
