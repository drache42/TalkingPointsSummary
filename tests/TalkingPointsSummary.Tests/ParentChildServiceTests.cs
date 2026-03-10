using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class ParentChildServiceTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateParentAsync_NormalizesAndPersistsParent()
    {
        await using var db = CreateDbContext();
        var service = new ParentService(db, new FixedTimeProvider(FixedUtcNow));

        var parent = await service.CreateParentAsync(
            new CreateParentRequest(
                "  Froehlich Family  ",
                "  token-123  ",
                "  contact-456  ",
                " one@example.com ; two@example.com ; "));

        parent.Name.Should().Be("Froehlich Family");
        parent.TalkingPointsToken.Should().Be("token-123");
        parent.TalkingPointsContactId.Should().Be("contact-456");
        parent.EmailRecipients.Should().Be("one@example.com;two@example.com");
        parent.IsActive.Should().BeTrue();
        parent.CreatedAt.Should().Be(FixedUtcNow.UtcDateTime);

        db.Parents.Should().ContainSingle();
    }

    [Fact]
    public async Task CreateParentAsync_InvalidEmail_ThrowsValidationException()
    {
        await using var db = CreateDbContext();
        var service = new ParentService(db);

        var act = () => service.CreateParentAsync(
            new CreateParentRequest("Family", "token", "contact", "not-an-email"));

        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*not a valid email address*");
    }

    [Fact]
    public async Task CreateChildAsync_UsesCurrentSchoolYearWhenStartingYearOmitted()
    {
        await using var db = CreateDbContext();
        var parent = new Parent
        {
            Name = "Family",
            TalkingPointsToken = "token",
            TalkingPointsContactId = "contact",
            EmailRecipients = "parent@example.com"
        };
        db.Parents.Add(parent);
        await db.SaveChangesAsync();

        var service = new ChildService(db, new FixedTimeProvider(FixedUtcNow));

        var child = await service.CreateChildAsync(
            parent.Id,
            new CreateChildRequest("Clara", "Elementary", 0, null, null));

        child.StartingYear.Should().Be(GradeCalculator.GetCurrentSchoolYear(FixedUtcNow.UtcDateTime));
        child.Emoji.Should().Be("📚");
    }

    [Fact]
    public async Task CreateChildAsync_MissingParent_ThrowsNotFound()
    {
        await using var db = CreateDbContext();
        var service = new ChildService(db, new FixedTimeProvider(FixedUtcNow));

        var act = () => service.CreateChildAsync(
            999,
            new CreateChildRequest("Clara", "Elementary", 0, null, "📚"));

        await act.Should().ThrowAsync<EntityNotFoundException>()
            .WithMessage("*Parent with ID 999 was not found.*");
    }

    [Fact]
    public async Task CreateChildAsync_InvalidGrade_ThrowsValidationException()
    {
        await using var db = CreateDbContext();
        var parent = new Parent
        {
            Name = "Family",
            TalkingPointsToken = "token",
            TalkingPointsContactId = "contact",
            EmailRecipients = "parent@example.com"
        };
        db.Parents.Add(parent);
        await db.SaveChangesAsync();

        var service = new ChildService(db, new FixedTimeProvider(FixedUtcNow));

        var act = () => service.CreateChildAsync(
            parent.Id,
            new CreateChildRequest("Clara", "Elementary", 13, null, "📚"));

        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("Grade must be between 0 and 12.");
    }

    [Fact]
    public async Task UpdateChildAsync_WithExplicitStartingYear_PreservesHistoricalYear()
    {
        await using var db = CreateDbContext();
        var parent = new Parent
        {
            Name = "Family",
            TalkingPointsToken = "token",
            TalkingPointsContactId = "contact",
            EmailRecipients = "parent@example.com"
        };
        var child = new Child
        {
            Parent = parent,
            Name = "Clara",
            School = "Elementary",
            StartingGrade = 0,
            StartingYear = 2025,
            Emoji = "📚"
        };
        db.Add(parent);
        db.Add(child);
        await db.SaveChangesAsync();

        var service = new ChildService(db, new FixedTimeProvider(FixedUtcNow));

        var updatedChild = await service.UpdateChildAsync(
            parent.Id,
            child.Id,
            new UpdateChildRequest("Clara", "Elementary", 1, 2024, "🎓"));

        updatedChild.StartingYear.Should().Be(2024);
        updatedChild.StartingGrade.Should().Be(1);
        updatedChild.Emoji.Should().Be("🎓");
    }

    [Fact]
    public async Task DeleteParentAsync_RemovesParentAndChildren()
    {
        await using var db = CreateDbContext();
        var parent = new Parent
        {
            Name = "Family",
            TalkingPointsToken = "token",
            TalkingPointsContactId = "contact",
            EmailRecipients = "parent@example.com",
            Children =
            [
                new Child
                {
                    Name = "Clara",
                    School = "Elementary",
                    StartingGrade = 0,
                    StartingYear = 2025,
                    Emoji = "📚"
                }
            ]
        };
        db.Add(parent);
        await db.SaveChangesAsync();

        var service = new ParentService(db, new FixedTimeProvider(FixedUtcNow));

        await service.DeleteParentAsync(parent.Id);

        db.Parents.Should().BeEmpty();
        db.Children.Should().BeEmpty();
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }
}