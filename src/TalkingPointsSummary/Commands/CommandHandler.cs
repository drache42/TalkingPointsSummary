using System.CommandLine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Pipeline;

namespace TalkingPointsSummary.Commands;

/// <summary>
/// Defines and handles CLI commands for managing parents, children, and manual pipeline runs.
/// </summary>
public static class CommandHandler
{
    public static RootCommand BuildRootCommand(IServiceProvider services)
    {
        var rootCommand = new RootCommand("Talking Points Summary - Weekly school message digest");

        rootCommand.AddCommand(BuildAddParentCommand(services));
        rootCommand.AddCommand(BuildAddChildCommand(services));
        rootCommand.AddCommand(BuildListParentsCommand(services));
        rootCommand.AddCommand(BuildRemoveParentCommand(services));
        rootCommand.AddCommand(BuildRemoveChildCommand(services));
        rootCommand.AddCommand(BuildRunCommand(services));

        return rootCommand;
    }

    private static Command BuildAddParentCommand(IServiceProvider services)
    {
        var nameOption = new Option<string>("--name", "Parent family name") { IsRequired = true };
        var tokenOption = new Option<string>("--token", "TalkingPoints x-token") { IsRequired = true };
        var contactIdOption = new Option<string>("--contact-id", "TalkingPoints x-contactid") { IsRequired = true };
        var emailsOption = new Option<string>("--emails", "Semicolon-delimited email addresses") { IsRequired = true };

        var command = new Command("add-parent", "Register a new parent");
        command.AddOption(nameOption);
        command.AddOption(tokenOption);
        command.AddOption(contactIdOption);
        command.AddOption(emailsOption);

        command.SetHandler(async (name, token, contactId, emails) =>
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var parent = new Parent
            {
                Name = name,
                TalkingPointsToken = token,
                TalkingPointsContactId = contactId,
                EmailRecipients = emails,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            db.Parents.Add(parent);
            await db.SaveChangesAsync();

            Console.WriteLine($"Added parent '{name}' with ID {parent.Id}");
        }, nameOption, tokenOption, contactIdOption, emailsOption);

        return command;
    }

    private static Command BuildAddChildCommand(IServiceProvider services)
    {
        var parentIdOption = new Option<int>("--parent-id", "Parent ID") { IsRequired = true };
        var nameOption = new Option<string>("--name", "Child's name") { IsRequired = true };
        var schoolOption = new Option<string>("--school", "School name") { IsRequired = true };
        var gradeOption = new Option<int>("--grade", "Starting grade (0=Kindergarten)") { IsRequired = true };
        var yearOption = new Option<int>("--year", "Starting school year (e.g. 2025)") { IsRequired = true };
        var emojiOption = new Option<string>("--emoji", () => "📚", "Emoji for summary headings");

        var command = new Command("add-child", "Add a child to a parent");
        command.AddOption(parentIdOption);
        command.AddOption(nameOption);
        command.AddOption(schoolOption);
        command.AddOption(gradeOption);
        command.AddOption(yearOption);
        command.AddOption(emojiOption);

        command.SetHandler(async (parentId, name, school, grade, year, emoji) =>
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var parent = await db.Parents.FindAsync(parentId);
            if (parent == null)
            {
                Console.Error.WriteLine($"Parent with ID {parentId} not found");
                Environment.ExitCode = 1;
                return;
            }

            var child = new Child
            {
                ParentId = parentId,
                Name = name,
                School = school,
                StartingGrade = grade,
                StartingYear = year,
                Emoji = emoji
            };

            db.Children.Add(child);
            await db.SaveChangesAsync();

            Console.WriteLine($"Added child '{name}' (ID {child.Id}) to parent '{parent.Name}'");
        }, parentIdOption, nameOption, schoolOption, gradeOption, yearOption, emojiOption);

        return command;
    }

    private static Command BuildListParentsCommand(IServiceProvider services)
    {
        var command = new Command("list-parents", "List all parents and their children");

        command.SetHandler(async () =>
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var parents = await db.Parents
                .Include(p => p.Children)
                .OrderBy(p => p.Id)
                .ToListAsync();

            if (parents.Count == 0)
            {
                Console.WriteLine("No parents registered.");
                return;
            }

            foreach (var parent in parents)
            {
                var status = parent.IsActive ? "active" : "inactive";
                Console.WriteLine($"\n[{parent.Id}] {parent.Name} ({status})");
                Console.WriteLine($"    Emails: {parent.EmailRecipients}");
                Console.WriteLine($"    ContactId: {parent.TalkingPointsContactId}");

                if (parent.Children.Count == 0)
                {
                    Console.WriteLine("    Children: (none)");
                }
                else
                {
                    foreach (var child in parent.Children)
                    {
                        var gradeLabel = Services.GradeCalculator.GetCurrentGradeLabel(child, DateTime.UtcNow);
                        Console.WriteLine($"    {child.Emoji} [{child.Id}] {child.Name} — {child.School} — {gradeLabel}");
                    }
                }
            }
        });

        return command;
    }

    private static Command BuildRemoveParentCommand(IServiceProvider services)
    {
        var idOption = new Option<int>("--id", "Parent ID to remove") { IsRequired = true };

        var command = new Command("remove-parent", "Remove a parent and all associated data");
        command.AddOption(idOption);

        command.SetHandler(async (id) =>
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var parent = await db.Parents.FindAsync(id);
            if (parent == null)
            {
                Console.Error.WriteLine($"Parent with ID {id} not found");
                Environment.ExitCode = 1;
                return;
            }

            db.Parents.Remove(parent);
            await db.SaveChangesAsync();

            Console.WriteLine($"Removed parent '{parent.Name}' (ID {id}) and all associated data");
        }, idOption);

        return command;
    }

    private static Command BuildRemoveChildCommand(IServiceProvider services)
    {
        var idOption = new Option<int>("--id", "Child ID to remove") { IsRequired = true };

        var command = new Command("remove-child", "Remove a child");
        command.AddOption(idOption);

        command.SetHandler(async (id) =>
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var child = await db.Children.FindAsync(id);
            if (child == null)
            {
                Console.Error.WriteLine($"Child with ID {id} not found");
                Environment.ExitCode = 1;
                return;
            }

            db.Children.Remove(child);
            await db.SaveChangesAsync();

            Console.WriteLine($"Removed child '{child.Name}' (ID {id})");
        }, idOption);

        return command;
    }

    private static Command BuildRunCommand(IServiceProvider services)
    {
        var command = new Command("run", "Manually trigger the full pipeline for all active parents");

        command.SetHandler(async () =>
        {
            using var scope = services.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<WeeklyPipelineService>();

            Console.WriteLine("Starting manual pipeline run...");
            await pipeline.RunFullPipelineAsync();
            Console.WriteLine("Pipeline run complete.");
        });

        return command;
    }
}
