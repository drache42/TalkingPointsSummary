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

        rootCommand.Add(BuildAddParentCommand(services));
        rootCommand.Add(BuildAddChildCommand(services));
        rootCommand.Add(BuildListParentsCommand(services));
        rootCommand.Add(BuildRemoveParentCommand(services));
        rootCommand.Add(BuildRemoveChildCommand(services));
        rootCommand.Add(BuildRunCommand(services));
        rootCommand.Add(BuildCheckConfigCommand(services));

        return rootCommand;
    }

    private static Command BuildAddParentCommand(IServiceProvider services)
    {
        var nameOption = new Option<string>("--name") { Description = "Parent family name", Required = true };
        var tokenOption = new Option<string>("--token") { Description = "TalkingPoints x-token", Required = true };
        var contactIdOption = new Option<string>("--contact-id") { Description = "TalkingPoints x-contactid", Required = true };
        var emailsOption = new Option<string>("--emails") { Description = "Semicolon-delimited email addresses", Required = true };

        var command = new Command("add-parent", "Register a new parent");
        command.Add(nameOption);
        command.Add(tokenOption);
        command.Add(contactIdOption);
        command.Add(emailsOption);

        command.SetAction(async (parseResult) =>
        {
            var name = parseResult.GetValue(nameOption)!;
            var token = parseResult.GetValue(tokenOption)!;
            var contactId = parseResult.GetValue(contactIdOption)!;
            var emails = parseResult.GetValue(emailsOption)!;

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
        });

        return command;
    }

    private static Command BuildAddChildCommand(IServiceProvider services)
    {
        var parentIdOption = new Option<int>("--parent-id") { Description = "Parent ID", Required = true };
        var nameOption = new Option<string>("--name") { Description = "Child's name", Required = true };
        var schoolOption = new Option<string>("--school") { Description = "School name", Required = true };
        var gradeOption = new Option<int>("--grade") { Description = "Starting grade (0=Kindergarten)", Required = true };
        var yearOption = new Option<int>("--year") { Description = "Starting school year (e.g. 2025)", Required = true };
        var emojiOption = new Option<string>("--emoji") { Description = "Emoji for summary headings", DefaultValueFactory = _ => "📚" };

        var command = new Command("add-child", "Add a child to a parent");
        command.Add(parentIdOption);
        command.Add(nameOption);
        command.Add(schoolOption);
        command.Add(gradeOption);
        command.Add(yearOption);
        command.Add(emojiOption);

        command.SetAction(async (parseResult) =>
        {
            var parentId = parseResult.GetValue(parentIdOption);
            var name = parseResult.GetValue(nameOption)!;
            var school = parseResult.GetValue(schoolOption)!;
            var grade = parseResult.GetValue(gradeOption);
            var year = parseResult.GetValue(yearOption);
            var emoji = parseResult.GetValue(emojiOption)!;

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
        });

        return command;
    }

    private static Command BuildListParentsCommand(IServiceProvider services)
    {
        var command = new Command("list-parents", "List all parents and their children");

        command.SetAction(async (parseResult) =>
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
        var idOption = new Option<int>("--id") { Description = "Parent ID to remove", Required = true };

        var command = new Command("remove-parent", "Remove a parent and all associated data");
        command.Add(idOption);

        command.SetAction(async (parseResult) =>
        {
            var id = parseResult.GetValue(idOption);

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
        });

        return command;
    }

    private static Command BuildRemoveChildCommand(IServiceProvider services)
    {
        var idOption = new Option<int>("--id") { Description = "Child ID to remove", Required = true };

        var command = new Command("remove-child", "Remove a child");
        command.Add(idOption);

        command.SetAction(async (parseResult) =>
        {
            var id = parseResult.GetValue(idOption);

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
        });

        return command;
    }

    private static Command BuildRunCommand(IServiceProvider services)
    {
        var command = new Command("run", "Manually trigger the full pipeline for all active parents");

        command.SetAction(async (parseResult) =>
        {
            using var scope = services.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<WeeklyPipelineService>();

            Console.WriteLine("Starting manual pipeline run...");
            var result = await pipeline.TryRunFullPipelineAsync("manual-cli");
            if (result == PipelineRunStatus.AlreadyRunning)
            {
                Console.WriteLine("A pipeline run is already in progress.");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine("Pipeline run complete.");
        });

        return command;
    }

    private static Command BuildCheckConfigCommand(IServiceProvider services)
    {
        var command = new Command("check-config", "Verify all required secrets and external service connections");

        command.SetAction(async (parseResult) =>
        {
            Console.WriteLine("Checking configuration and connectivity...\n");

            using var scope = services.CreateScope();
            var validator = scope.ServiceProvider.GetRequiredService<TalkingPointsSummary.Services.StartupValidator>();
            var results = await validator.RunAllChecksAsync();

            var labelWidth = results.Max(r => r.Name.Length) + 2;

            foreach (var result in results)
            {
                var icon = result.Status switch
                {
                    TalkingPointsSummary.Services.CheckStatus.Pass => "✅ PASS",
                    TalkingPointsSummary.Services.CheckStatus.Warn => "⚠️  WARN",
                    TalkingPointsSummary.Services.CheckStatus.Fail => "❌ FAIL",
                    _ => "     "
                };
                Console.WriteLine($"{icon}  {result.Name.PadRight(labelWidth)}{result.Detail}");
            }

            var failCount = results.Count(r => r.Status == TalkingPointsSummary.Services.CheckStatus.Fail);
            Console.WriteLine();

            if (failCount == 0)
            {
                Console.WriteLine("All checks passed.");
            }
            else
            {
                Console.Error.WriteLine($"{failCount} check(s) failed. Review the output above.");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }
}
