using Microsoft.EntityFrameworkCore;
using TalkingPointsSummary.Admin;
using TalkingPointsSummary.Admin.Configuration;
using TalkingPointsSummary.Admin.Services;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Services;

var builder = WebApplication.CreateBuilder(args);

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
