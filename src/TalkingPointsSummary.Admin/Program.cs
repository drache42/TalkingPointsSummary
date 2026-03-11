using Microsoft.EntityFrameworkCore;
using TalkingPointsSummary.Admin;
using TalkingPointsSummary.Admin.Configuration;
using TalkingPointsSummary.Admin.Services;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Services;

var builder = WebApplication.CreateBuilder(args);
var debugFeaturesEnabled = AdminServiceConfiguration.AreDebugFeaturesEnabled(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

AdminDataProtectionConfiguration.ConfigureDataProtection(
    builder.Services,
    builder.Configuration,
    AdminDataProtectionConfiguration.IsRunningInContainer());

AdminServiceConfiguration.ConfigureApplicationServices(builder.Services, builder.Configuration);

var app = builder.Build();

await EnsureDatabaseReadyAsync(app.Services, app.Lifetime.ApplicationStopping);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

if (!debugFeaturesEnabled)
{
    app.Use(async (context, next) =>
    {
        if (AdminServiceConfiguration.ShouldBlockDebugRoute(context.Request.Path, debugFeaturesEnabled))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next();
    });
}

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static async Task EnsureDatabaseReadyAsync(IServiceProvider services, CancellationToken cancellationToken)
{
    const int maxAttempts = 3;
    const int delayMs = 1000;

    using var scope = services.CreateScope();
    var logger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("TalkingPointsSummary.Admin.Startup");

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await using var validationScope = services.CreateAsyncScope();
            var dbFactory = validationScope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                throw new InvalidOperationException("Admin database connectivity check returned false.");
            }

            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            logger.LogWarning(
                ex,
                "Admin database connectivity check failed on attempt {Attempt}/{MaxAttempts}. Retrying in {DelayMs}ms.",
                attempt,
                maxAttempts,
                delayMs);

            await Task.Delay(delayMs, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Admin database connectivity check failed after {MaxAttempts} attempts.", maxAttempts);
            throw;
        }
    }
}
