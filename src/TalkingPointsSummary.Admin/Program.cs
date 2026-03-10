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

AdminServiceConfiguration.ConfigureApplicationServices(builder.Services, builder.Configuration);

var app = builder.Build();

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
