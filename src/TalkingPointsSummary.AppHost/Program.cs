using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<ProjectResource> worker;
IResourceBuilder<ProjectResource> admin;

if (builder.Configuration.GetValue<bool>("ManagePostgres", true))
{
    // Aspire manages a local PostgreSQL container (default for Docker-based dev machines)
    var postgresPassword = builder.AddParameter("postgres-password", secret: true);

    var postgres = builder.AddPostgres("postgres", password: postgresPassword)
        .WithImage("postgres", "15-alpine")
        .WithHostPort(5432)
        .WithDataVolume("talkingpoints-postgres-data")
        .WithPgAdmin();

    var db = postgres.AddDatabase("talkingpoints");

    worker = builder.AddProject<Projects.TalkingPointsSummary>("worker")
        .WithReference(db)
        .WaitFor(db)
        .WithEnvironment("CONNECTION_STRING", db)
        .WithEnvironment("ASPNETCORE_URLS", "http://127.0.0.1:5101");

    admin = builder.AddProject<Projects.TalkingPointsSummary_Admin>("admin")
        .WithReference(db)
        .WaitFor(db)
        .WithEnvironment("CONNECTION_STRING", db)
        .WithEnvironment("WorkerDebugBaseUrl", "http://127.0.0.1:5101/");
}
else
{
    // External PostgreSQL — supply connection string via appsettings.json or user secrets:
    //   "ConnectionStrings": { "postgres": "Host=...;Database=...;Username=...;Password=..." }
    var postgres = builder.AddConnectionString("postgres");

    worker = builder.AddProject<Projects.TalkingPointsSummary>("worker")
        .WithEnvironment("CONNECTION_STRING", postgres)
        .WithEnvironment("ASPNETCORE_URLS", "http://127.0.0.1:5101");

    admin = builder.AddProject<Projects.TalkingPointsSummary_Admin>("admin")
        .WithEnvironment("CONNECTION_STRING", postgres)
        .WithEnvironment("WorkerDebugBaseUrl", "http://127.0.0.1:5101/");
}

if (builder.Configuration.GetValue<bool>("ManageBrowserless", true))
{
    // Aspire manages a local Browserless container (default for Docker-based dev machines)
    var browserless = builder.AddContainer("browserless", "browserless/chrome")
        .WithHttpEndpoint(targetPort: 3000)
        .WithEnvironment("MAX_CONCURRENT_SESSIONS", "2")
        .WithEnvironment("MAX_QUEUE_LENGTH", "5");

    var browserlessEndpoint = browserless.GetEndpoint("http");

    worker
        .WaitFor(browserless)
        .WithEnvironment("BROWSERLESS_HOST", browserlessEndpoint.Property(EndpointProperty.Host))
        .WithEnvironment("BROWSERLESS_PORT", browserlessEndpoint.Property(EndpointProperty.Port));
}
else
{
    var browserlessUrl = builder.Configuration["BrowserlessUrl"];
    if (string.IsNullOrWhiteSpace(browserlessUrl))
    {
        throw new InvalidOperationException(
            "ManageBrowserless is false, but BrowserlessUrl is not configured. Set BrowserlessUrl in AppHost configuration or user secrets.");
    }

    worker.WithEnvironment("BROWSERLESS_URL", browserlessUrl);
}

if (builder.Configuration.GetValue<bool>("ManageMailpit", true))
{
    // Aspire manages a local Mailpit container — catches all outgoing emails for dev inspection
    // Browse captured emails at http://localhost:8025
    var mailpit = builder.AddContainer("mailpit", "axllent/mailpit")
        .WithHttpEndpoint(port: 8025, targetPort: 8025, name: "ui")
        .WithEndpoint(port: 1025, targetPort: 1025, name: "smtp", scheme: "tcp");

    var smtpEndpoint = mailpit.GetEndpoint("smtp");

    worker
        .WaitFor(mailpit)
        .WithEnvironment("SMTP_HOST", smtpEndpoint.Property(EndpointProperty.Host))
        .WithEnvironment("SMTP_PORT", smtpEndpoint.Property(EndpointProperty.Port));
}

// Pass optional CLI args to the worker (e.g. "run" or "check-config" set via AppHost launch profile)
var workerArgs = builder.Configuration["WorkerArgs"];
if (!string.IsNullOrWhiteSpace(workerArgs))
    worker.WithArgs(workerArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));

builder.Build().Run();
