using System.ComponentModel.DataAnnotations;
using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;

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
            var parentService = scope.ServiceProvider.GetRequiredService<IParentService>();

            try
            {
                var parent = await parentService.CreateParentAsync(
                    new CreateParentRequest(name, token, contactId, emails));
                Console.WriteLine($"Added parent '{parent.Name}' with ID {parent.Id}");
            }
            catch (Exception ex) when (HandleCommandError(ex))
            {
            }
        });

        return command;
    }

    private static Command BuildAddChildCommand(IServiceProvider services)
    {
        var parentIdOption = new Option<int>("--parent-id") { Description = "Parent ID", Required = true };
        var nameOption = new Option<string>("--name") { Description = "Child's name", Required = true };
        var schoolOption = new Option<string>("--school") { Description = "School name", Required = true };
        var gradeOption = new Option<int>("--grade") { Description = "Starting grade (0=Kindergarten)", Required = true };
        var yearOption = new Option<int?>("--year") { Description = "Starting school year (e.g. 2025). Omit to use the current school year." };
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
            var childService = scope.ServiceProvider.GetRequiredService<IChildService>();
            var parentService = scope.ServiceProvider.GetRequiredService<IParentService>();

            try
            {
                var child = await childService.CreateChildAsync(
                    parentId,
                    new CreateChildRequest(name, school, grade, year, emoji));
                var parent = await parentService.GetParentAsync(parentId);
                Console.WriteLine($"Added child '{child.Name}' (ID {child.Id}) to parent '{parent?.Name ?? parentId.ToString()}'");
            }
            catch (Exception ex) when (HandleCommandError(ex))
            {
            }
        });

        return command;
    }

    private static Command BuildListParentsCommand(IServiceProvider services)
    {
        var command = new Command("list-parents", "List all parents and their children");

        command.SetAction(async (parseResult) =>
        {
            using var scope = services.CreateScope();
            var parentService = scope.ServiceProvider.GetRequiredService<IParentService>();
            var parents = await parentService.ListParentsAsync();

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
            var parentService = scope.ServiceProvider.GetRequiredService<IParentService>();

            try
            {
                var parent = await parentService.GetParentAsync(id);
                await parentService.DeleteParentAsync(id);
                Console.WriteLine($"Removed parent '{parent?.Name ?? id.ToString()}' (ID {id}) and all associated data");
            }
            catch (Exception ex) when (HandleCommandError(ex))
            {
            }
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
            var parentService = scope.ServiceProvider.GetRequiredService<IParentService>();
            var parent = (await parentService.ListParentsAsync())
                .FirstOrDefault(candidateParent => candidateParent.Children.Any(child => child.Id == id));
            if (parent == null)
            {
                Console.Error.WriteLine($"Child with ID {id} not found");
                Environment.ExitCode = 1;
                return;
            }

            var child = parent.Children.First(child => child.Id == id);
            var childService = scope.ServiceProvider.GetRequiredService<IChildService>();

            try
            {
                await childService.DeleteChildAsync(parent.Id, id);
                Console.WriteLine($"Removed child '{child.Name}' (ID {id})");
            }
            catch (Exception ex) when (HandleCommandError(ex))
            {
            }
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

    private static bool HandleCommandError(Exception ex)
    {
        switch (ex)
        {
            case ValidationException:
            case EntityNotFoundException:
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
                return true;
            default:
                return false;
        }
    }
}
