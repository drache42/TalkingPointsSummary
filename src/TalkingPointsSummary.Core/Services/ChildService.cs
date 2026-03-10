using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

public sealed record CreateChildRequest(
    string Name,
    string School,
    int Grade,
    int? StartingYear,
    string? Emoji = null);

public sealed record UpdateChildRequest(
    string Name,
    string School,
    int Grade,
    int? StartingYear,
    string? Emoji = null);

public interface IChildService
{
    Task<Child> CreateChildAsync(int parentId, CreateChildRequest request, CancellationToken cancellationToken = default);
    Task<Child> UpdateChildAsync(int parentId, int childId, UpdateChildRequest request, CancellationToken cancellationToken = default);
    Task DeleteChildAsync(int parentId, int childId, CancellationToken cancellationToken = default);
    Task<Child?> GetChildAsync(int parentId, int childId, CancellationToken cancellationToken = default);
    Task<List<Child>> ListChildrenAsync(int parentId, CancellationToken cancellationToken = default);
}

public sealed class ChildService(AppDbContext dbContext) : IChildService
{
    public async Task<Child> CreateChildAsync(int parentId, CreateChildRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureParentExistsAsync(parentId, cancellationToken);

        var child = new Child
        {
            ParentId = parentId,
            Name = NormalizeRequired(request.Name, 200, nameof(request.Name)),
            School = NormalizeRequired(request.School, 300, nameof(request.School)),
            StartingGrade = ValidateGrade(request.Grade),
            StartingYear = ResolveStartingYear(request.StartingYear),
            Emoji = NormalizeEmoji(request.Emoji)
        };

        dbContext.Children.Add(child);
        await dbContext.SaveChangesAsync(cancellationToken);
        return child;
    }

    public async Task<Child> UpdateChildAsync(int parentId, int childId, UpdateChildRequest request, CancellationToken cancellationToken = default)
    {
        var child = await dbContext.Children
            .FirstOrDefaultAsync(existingChild => existingChild.Id == childId && existingChild.ParentId == parentId, cancellationToken);

        if (child is null)
        {
            throw new EntityNotFoundException($"Child with ID {childId} for parent {parentId} was not found.");
        }

        child.Name = NormalizeRequired(request.Name, 200, nameof(request.Name));
        child.School = NormalizeRequired(request.School, 300, nameof(request.School));
        child.StartingGrade = ValidateGrade(request.Grade);
        child.StartingYear = ResolveStartingYear(request.StartingYear);
        child.Emoji = NormalizeEmoji(request.Emoji);

        await dbContext.SaveChangesAsync(cancellationToken);
        return child;
    }

    public async Task DeleteChildAsync(int parentId, int childId, CancellationToken cancellationToken = default)
    {
        var child = await dbContext.Children
            .FirstOrDefaultAsync(existingChild => existingChild.Id == childId && existingChild.ParentId == parentId, cancellationToken);

        if (child is null)
        {
            throw new EntityNotFoundException($"Child with ID {childId} for parent {parentId} was not found.");
        }

        dbContext.Children.Remove(child);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<Child?> GetChildAsync(int parentId, int childId, CancellationToken cancellationToken = default)
    {
        return dbContext.Children
            .FirstOrDefaultAsync(child => child.Id == childId && child.ParentId == parentId, cancellationToken);
    }

    public Task<List<Child>> ListChildrenAsync(int parentId, CancellationToken cancellationToken = default)
    {
        return dbContext.Children
            .Where(child => child.ParentId == parentId)
            .OrderBy(child => child.Name)
            .ToListAsync(cancellationToken);
    }

    private async Task EnsureParentExistsAsync(int parentId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.Parents.AnyAsync(parent => parent.Id == parentId, cancellationToken);
        if (!exists)
        {
            throw new EntityNotFoundException($"Parent with ID {parentId} was not found.");
        }
    }

    private static string NormalizeRequired(string value, int maxLength, string fieldName)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ValidationException($"{fieldName} is required.");
        }

        if (normalized.Length > maxLength)
        {
            throw new ValidationException($"{fieldName} must be {maxLength} characters or fewer.");
        }

        return normalized;
    }

    private static int ValidateGrade(int grade)
    {
        if (grade is < 0 or > 12)
        {
            throw new ValidationException("Grade must be between 0 and 12.");
        }

        return grade;
    }

    private static int ResolveStartingYear(int? requestedStartingYear)
    {
        var currentSchoolYear = GradeCalculator.GetCurrentSchoolYear(DateTime.UtcNow);
        var resolvedYear = requestedStartingYear ?? currentSchoolYear;

        if (resolvedYear < 2000 || resolvedYear > currentSchoolYear)
        {
            throw new ValidationException($"StartingYear must be between 2000 and {currentSchoolYear}.");
        }

        return resolvedYear;
    }

    private static string NormalizeEmoji(string? emoji)
    {
        var normalized = string.IsNullOrWhiteSpace(emoji) ? "📚" : emoji.Trim();
        if (normalized.Length > 10)
        {
            throw new ValidationException("Emoji must be 10 characters or fewer.");
        }

        return normalized;
    }
}