using Microsoft.EntityFrameworkCore;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Parent> Parents => Set<Parent>();
    public DbSet<Child> Children => Set<Child>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<NewsItem> NewsItems => Set<NewsItem>();
    public DbSet<Summary> Summaries => Set<Summary>();
    public DbSet<PipelineRun> PipelineRuns => Set<PipelineRun>();

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
