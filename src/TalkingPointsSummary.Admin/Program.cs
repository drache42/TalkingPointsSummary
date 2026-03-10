using Microsoft.EntityFrameworkCore;
using TalkingPointsSummary.Admin;
using TalkingPointsSummary.Admin.Services;
using TalkingPointsSummary.Data;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("TalkingPoints")
    ?? throw new InvalidOperationException(
        "Missing required connection string 'TalkingPoints'. Configure ConnectionStrings:TalkingPoints via appsettings, user secrets, or environment variables.");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHttpClient<PipelineDebugClient>(client =>
{
    var workerDebugBaseUrl = builder.Configuration["WorkerDebugBaseUrl"];
    if (Uri.TryCreate(workerDebugBaseUrl, UriKind.Absolute, out var baseAddress))
    {
        client.BaseAddress = baseAddress;
    }
});

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
