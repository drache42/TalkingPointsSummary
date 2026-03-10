using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Request payload for creating a parent record.
/// </summary>
public sealed record CreateParentRequest
{
    /// <summary>
    /// Initializes a new create-parent request.
    /// </summary>
    /// <param name="name">Display name for the parent.</param>
    /// <param name="talkingPointsToken">TalkingPoints API token for the parent.</param>
    /// <param name="talkingPointsContactId">TalkingPoints contact identifier for the parent.</param>
    /// <param name="emailRecipients">Semicolon-delimited recipient list for summary emails.</param>
    /// <param name="isActive">Whether the parent is eligible for pipeline processing.</param>
    public CreateParentRequest(string name, string talkingPointsToken, string talkingPointsContactId, string emailRecipients, bool isActive = true)
    {
        Name = name;
        TalkingPointsToken = talkingPointsToken;
        TalkingPointsContactId = talkingPointsContactId;
        EmailRecipients = emailRecipients;
        IsActive = isActive;
    }

    /// <summary>
    /// Display name for the parent.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// TalkingPoints API token for the parent.
    /// </summary>
    public string TalkingPointsToken { get; init; }

    /// <summary>
    /// TalkingPoints contact identifier for the parent.
    /// </summary>
    public string TalkingPointsContactId { get; init; }

    /// <summary>
    /// Semicolon-delimited recipient list for summary emails.
    /// </summary>
    public string EmailRecipients { get; init; }

    /// <summary>
    /// Whether the parent is eligible for pipeline processing.
    /// </summary>
    public bool IsActive { get; init; }
}

/// <summary>
/// Request payload for updating an existing parent record.
/// </summary>
public sealed record UpdateParentRequest
{
    /// <summary>
    /// Initializes a new update-parent request.
    /// </summary>
    /// <param name="name">Display name for the parent.</param>
    /// <param name="talkingPointsToken">TalkingPoints API token for the parent.</param>
    /// <param name="talkingPointsContactId">TalkingPoints contact identifier for the parent.</param>
    /// <param name="emailRecipients">Semicolon-delimited recipient list for summary emails.</param>
    /// <param name="isActive">Whether the parent is eligible for pipeline processing.</param>
    public UpdateParentRequest(string name, string talkingPointsToken, string talkingPointsContactId, string emailRecipients, bool isActive)
    {
        Name = name;
        TalkingPointsToken = talkingPointsToken;
        TalkingPointsContactId = talkingPointsContactId;
        EmailRecipients = emailRecipients;
        IsActive = isActive;
    }

    /// <summary>
    /// Display name for the parent.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// TalkingPoints API token for the parent.
    /// </summary>
    public string TalkingPointsToken { get; init; }

    /// <summary>
    /// TalkingPoints contact identifier for the parent.
    /// </summary>
    public string TalkingPointsContactId { get; init; }

    /// <summary>
    /// Semicolon-delimited recipient list for summary emails.
    /// </summary>
    public string EmailRecipients { get; init; }

    /// <summary>
    /// Whether the parent is eligible for pipeline processing.
    /// </summary>
    public bool IsActive { get; init; }
}

/// <summary>
/// CRUD operations for parent records.
/// </summary>
public interface IParentService
{
    /// <summary>
    /// Creates and persists a normalized parent record.
    /// </summary>
    /// <param name="request">The values to store for the new parent.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<Parent> CreateParentAsync(CreateParentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing parent record.
    /// </summary>
    /// <param name="id">Identifier of the parent to update.</param>
    /// <param name="request">The replacement values for the parent.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<Parent> UpdateParentAsync(int id, UpdateParentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an existing parent record.
    /// </summary>
    /// <param name="id">Identifier of the parent to delete.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task DeleteParentAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a parent by identifier, including children, or <see langword="null"/> when missing.
    /// </summary>
    /// <param name="id">Identifier of the parent to fetch.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<Parent?> GetParentAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all parents ordered by name, including their children.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<List<Parent>> ListParentsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Entity Framework-backed implementation of <see cref="IParentService"/>.
/// </summary>
/// <param name="dbContext">Database context used for persistence.</param>
/// <param name="timeProvider">Optional time provider used when stamping new parents.</param>
public sealed class ParentService(AppDbContext dbContext, TimeProvider? timeProvider = null) : IParentService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Creates and persists a normalized parent record.
    /// </summary>
    /// <param name="request">The values to store for the new parent.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public async Task<Parent> CreateParentAsync(CreateParentRequest request, CancellationToken cancellationToken = default)
    {
        var parent = new Parent
        {
            Name = NormalizeRequired(request.Name, 200, nameof(request.Name)),
            TalkingPointsToken = NormalizeRequired(request.TalkingPointsToken, null, nameof(request.TalkingPointsToken)),
            TalkingPointsContactId = NormalizeRequired(request.TalkingPointsContactId, 100, nameof(request.TalkingPointsContactId)),
            EmailRecipients = NormalizeEmailRecipients(request.EmailRecipients),
            IsActive = request.IsActive,
            CreatedAt = _timeProvider.GetUtcDateTime()
        };

        dbContext.Parents.Add(parent);
        await dbContext.SaveChangesAsync(cancellationToken);
        return parent;
    }

    /// <summary>
    /// Updates an existing parent record.
    /// </summary>
    /// <param name="id">Identifier of the parent to update.</param>
    /// <param name="request">The replacement values for the parent.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public async Task<Parent> UpdateParentAsync(int id, UpdateParentRequest request, CancellationToken cancellationToken = default)
    {
        var parent = await dbContext.Parents.FindAsync([id], cancellationToken);
        if (parent is null)
        {
            throw new EntityNotFoundException($"Parent with ID {id} was not found.");
        }

        parent.Name = NormalizeRequired(request.Name, 200, nameof(request.Name));
        parent.TalkingPointsToken = NormalizeRequired(request.TalkingPointsToken, null, nameof(request.TalkingPointsToken));
        parent.TalkingPointsContactId = NormalizeRequired(request.TalkingPointsContactId, 100, nameof(request.TalkingPointsContactId));
        parent.EmailRecipients = NormalizeEmailRecipients(request.EmailRecipients);
        parent.IsActive = request.IsActive;

        await dbContext.SaveChangesAsync(cancellationToken);
        return parent;
    }

    /// <summary>
    /// Deletes an existing parent record.
    /// </summary>
    /// <param name="id">Identifier of the parent to delete.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public async Task DeleteParentAsync(int id, CancellationToken cancellationToken = default)
    {
        var parent = await dbContext.Parents.FindAsync([id], cancellationToken);
        if (parent is null)
        {
            throw new EntityNotFoundException($"Parent with ID {id} was not found.");
        }

        dbContext.Parents.Remove(parent);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Returns a parent by identifier, including children, or <see langword="null"/> when missing.
    /// </summary>
    /// <param name="id">Identifier of the parent to fetch.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public Task<Parent?> GetParentAsync(int id, CancellationToken cancellationToken = default)
    {
        return dbContext.Parents
            .Include(parent => parent.Children)
            .FirstOrDefaultAsync(parent => parent.Id == id, cancellationToken);
    }

    /// <summary>
    /// Lists all parents ordered by name, including their children.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public Task<List<Parent>> ListParentsAsync(CancellationToken cancellationToken = default)
    {
        return dbContext.Parents
            .Include(parent => parent.Children)
            .OrderBy(parent => parent.Name)
            .ToListAsync(cancellationToken);
    }

    private static string NormalizeRequired(string value, int? maxLength, string fieldName)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ValidationException($"{fieldName} is required.");
        }

        if (maxLength is int max && normalized.Length > max)
        {
            throw new ValidationException($"{fieldName} must be {max} characters or fewer.");
        }

        return normalized;
    }

    private static string NormalizeEmailRecipients(string value)
    {
        var normalizedRecipients = value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(recipient => !string.IsNullOrWhiteSpace(recipient))
            .ToArray();

        if (normalizedRecipients.Length == 0)
        {
            throw new ValidationException("EmailRecipients must contain at least one email address.");
        }

        foreach (var recipient in normalizedRecipients)
        {
            if (!new EmailAddressAttribute().IsValid(recipient))
            {
                throw new ValidationException($"'{recipient}' is not a valid email address.");
            }
        }

        return string.Join(';', normalizedRecipients);
    }
}

/// <summary>
/// Dependency injection registrations for parent and child CRUD services.
/// </summary>
public static class ParentChildServiceCollectionExtensions
{
    /// <summary>
    /// Registers parent, child, and grade-calculation services.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    public static IServiceCollection AddParentChildServices(this IServiceCollection services)
    {
        services.AddSingleton<IGradeCalculator, GradeCalculator>();
        services.AddScoped<IParentService, ParentService>();
        services.AddScoped<IChildService, ChildService>();
        return services;
    }
}