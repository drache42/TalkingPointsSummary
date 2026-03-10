using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Request payload for creating a child record under a parent.
/// </summary>
public sealed record CreateChildRequest
{
    /// <summary>
    /// Initializes a new create-child request.
    /// </summary>
    /// <param name="name">Child display name.</param>
    /// <param name="school">School attended by the child.</param>
    /// <param name="grade">Starting grade level for the child.</param>
    /// <param name="startingYear">Optional school year corresponding to the starting grade.</param>
    /// <param name="emoji">Optional emoji shown for the child in summaries and UI.</param>
    public CreateChildRequest(string name, string school, int grade, int? startingYear, string? emoji = null)
    {
        Name = name;
        School = school;
        Grade = grade;
        StartingYear = startingYear;
        Emoji = emoji;
    }

    /// <summary>
    /// Child display name.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// School attended by the child.
    /// </summary>
    public string School { get; init; }

    /// <summary>
    /// Starting grade level for the child.
    /// </summary>
    public int Grade { get; init; }

    /// <summary>
    /// Optional school year corresponding to the starting grade.
    /// </summary>
    public int? StartingYear { get; init; }

    /// <summary>
    /// Optional emoji shown for the child in summaries and UI.
    /// </summary>
    public string? Emoji { get; init; }
}

/// <summary>
/// Request payload for updating an existing child record.
/// </summary>
public sealed record UpdateChildRequest
{
    /// <summary>
    /// Initializes a new update-child request.
    /// </summary>
    /// <param name="name">Child display name.</param>
    /// <param name="school">School attended by the child.</param>
    /// <param name="grade">Starting grade level for the child.</param>
    /// <param name="startingYear">Optional school year corresponding to the starting grade.</param>
    /// <param name="emoji">Optional emoji shown for the child in summaries and UI.</param>
    public UpdateChildRequest(string name, string school, int grade, int? startingYear, string? emoji = null)
    {
        Name = name;
        School = school;
        Grade = grade;
        StartingYear = startingYear;
        Emoji = emoji;
    }

    /// <summary>
    /// Child display name.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// School attended by the child.
    /// </summary>
    public string School { get; init; }

    /// <summary>
    /// Starting grade level for the child.
    /// </summary>
    public int Grade { get; init; }

    /// <summary>
    /// Optional school year corresponding to the starting grade.
    /// </summary>
    public int? StartingYear { get; init; }

    /// <summary>
    /// Optional emoji shown for the child in summaries and UI.
    /// </summary>
    public string? Emoji { get; init; }
}

/// <summary>
/// CRUD operations for child records scoped to a parent.
/// </summary>
public interface IChildService
{
    /// <summary>
    /// Creates a child record for the specified parent.
    /// </summary>
    /// <param name="parentId">Identifier of the parent that owns the child.</param>
    /// <param name="request">The values to store for the new child.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<Child> CreateChildAsync(int parentId, CreateChildRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing child record for the specified parent.
    /// </summary>
    /// <param name="parentId">Identifier of the parent that owns the child.</param>
    /// <param name="childId">Identifier of the child to update.</param>
    /// <param name="request">The replacement values for the child.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<Child> UpdateChildAsync(int parentId, int childId, UpdateChildRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a child record for the specified parent.
    /// </summary>
    /// <param name="parentId">Identifier of the parent that owns the child.</param>
    /// <param name="childId">Identifier of the child to delete.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task DeleteChildAsync(int parentId, int childId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a child by identifier within a parent scope, or <see langword="null"/> when missing.
    /// </summary>
    /// <param name="parentId">Identifier of the parent that owns the child.</param>
    /// <param name="childId">Identifier of the child to fetch.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<Child?> GetChildAsync(int parentId, int childId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all children for a parent ordered by name.
    /// </summary>
    /// <param name="parentId">Identifier of the parent whose children should be returned.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<List<Child>> ListChildrenAsync(int parentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Entity Framework-backed implementation of <see cref="IChildService"/>.
/// </summary>
/// <param name="dbContext">Database context used for persistence.</param>
/// <param name="gradeCalculator">Optional calculator used to resolve school years.</param>
public sealed class ChildService(AppDbContext dbContext, IGradeCalculator? gradeCalculator = null) : IChildService
{
    private readonly IGradeCalculator _gradeCalculator = gradeCalculator ?? new GradeCalculator();

    /// <summary>
    /// Creates a child record for the specified parent.
    /// </summary>
    /// <param name="parentId">Identifier of the parent that owns the child.</param>
    /// <param name="request">The values to store for the new child.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
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

    /// <summary>
    /// Updates an existing child record for the specified parent.
    /// </summary>
    /// <param name="parentId">Identifier of the parent that owns the child.</param>
    /// <param name="childId">Identifier of the child to update.</param>
    /// <param name="request">The replacement values for the child.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
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

    /// <summary>
    /// Deletes a child record for the specified parent.
    /// </summary>
    /// <param name="parentId">Identifier of the parent that owns the child.</param>
    /// <param name="childId">Identifier of the child to delete.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
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

    /// <summary>
    /// Returns a child by identifier within a parent scope, or <see langword="null"/> when missing.
    /// </summary>
    /// <param name="parentId">Identifier of the parent that owns the child.</param>
    /// <param name="childId">Identifier of the child to fetch.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public Task<Child?> GetChildAsync(int parentId, int childId, CancellationToken cancellationToken = default)
    {
        return dbContext.Children
            .FirstOrDefaultAsync(child => child.Id == childId && child.ParentId == parentId, cancellationToken);
    }

    /// <summary>
    /// Lists all children for a parent ordered by name.
    /// </summary>
    /// <param name="parentId">Identifier of the parent whose children should be returned.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
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

    private int ResolveStartingYear(int? requestedStartingYear)
    {
        var currentSchoolYear = _gradeCalculator.GetCurrentSchoolYear();
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