using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

public sealed record CreateParentRequest(
    string Name,
    string TalkingPointsToken,
    string TalkingPointsContactId,
    string EmailRecipients,
    bool IsActive = true);

public sealed record UpdateParentRequest(
    string Name,
    string TalkingPointsToken,
    string TalkingPointsContactId,
    string EmailRecipients,
    bool IsActive);

public interface IParentService
{
    Task<Parent> CreateParentAsync(CreateParentRequest request, CancellationToken cancellationToken = default);
    Task<Parent> UpdateParentAsync(int id, UpdateParentRequest request, CancellationToken cancellationToken = default);
    Task DeleteParentAsync(int id, CancellationToken cancellationToken = default);
    Task<Parent?> GetParentAsync(int id, CancellationToken cancellationToken = default);
    Task<List<Parent>> ListParentsAsync(CancellationToken cancellationToken = default);
}

public sealed class ParentService(AppDbContext dbContext) : IParentService
{
    public async Task<Parent> CreateParentAsync(CreateParentRequest request, CancellationToken cancellationToken = default)
    {
        var parent = new Parent
        {
            Name = NormalizeRequired(request.Name, 200, nameof(request.Name)),
            TalkingPointsToken = NormalizeRequired(request.TalkingPointsToken, null, nameof(request.TalkingPointsToken)),
            TalkingPointsContactId = NormalizeRequired(request.TalkingPointsContactId, 100, nameof(request.TalkingPointsContactId)),
            EmailRecipients = NormalizeEmailRecipients(request.EmailRecipients),
            IsActive = request.IsActive,
            CreatedAt = DateTime.UtcNow
        };

        dbContext.Parents.Add(parent);
        await dbContext.SaveChangesAsync(cancellationToken);
        return parent;
    }

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

    public Task<Parent?> GetParentAsync(int id, CancellationToken cancellationToken = default)
    {
        return dbContext.Parents
            .Include(parent => parent.Children)
            .FirstOrDefaultAsync(parent => parent.Id == id, cancellationToken);
    }

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

public static class ParentChildServiceCollectionExtensions
{
    public static IServiceCollection AddParentChildServices(this IServiceCollection services)
    {
        services.AddScoped<IParentService, ParentService>();
        services.AddScoped<IChildService, ChildService>();
        return services;
    }
}