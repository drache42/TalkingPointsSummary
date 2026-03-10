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
        .WithEnvironment("ConnectionStrings__TalkingPoints", db)
        .WithEnvironment("ASPNETCORE_URLS", "http://127.0.0.1:5101")
        .WithEnvironment("DebugFeatures__Enabled", "true");

    admin = builder.AddProject<Projects.TalkingPointsSummary_Admin>("admin")
        .WithReference(db)
        .WaitFor(db)
        .WithEnvironment("ConnectionStrings__TalkingPoints", db)
        .WithEnvironment("DebugFeatures__Enabled", "true")
        .WithEnvironment("WorkerDebugBaseUrl", "http://127.0.0.1:5101/");
}
else
{
    // External PostgreSQL — supply connection string via appsettings.json or user secrets:
    //   "ConnectionStrings": { "TalkingPoints": "Host=...;Database=...;Username=...;Password=..." }
    var postgres = builder.AddConnectionString("TalkingPoints");

    worker = builder.AddProject<Projects.TalkingPointsSummary>("worker")
        .WithEnvironment("ConnectionStrings__TalkingPoints", postgres)
        .WithEnvironment("ASPNETCORE_URLS", "http://127.0.0.1:5101")
        .WithEnvironment("DebugFeatures__Enabled", "true");

    admin = builder.AddProject<Projects.TalkingPointsSummary_Admin>("admin")
        .WithEnvironment("ConnectionStrings__TalkingPoints", postgres)
        .WithEnvironment("DebugFeatures__Enabled", "true")
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
        .WithEnvironment("Browserless__BaseUrl", browserlessEndpoint.Property(EndpointProperty.Url));
}
else
{
    var browserlessUrl = builder.Configuration["Browserless:BaseUrl"];
    if (string.IsNullOrWhiteSpace(browserlessUrl))
    {
        throw new InvalidOperationException(
            "ManageBrowserless is false, but Browserless:BaseUrl is not configured. Set Browserless:BaseUrl in AppHost configuration or user secrets.");
    }

    worker.WithEnvironment("Browserless__BaseUrl", browserlessUrl);
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
        .WithEnvironment("Smtp__Host", smtpEndpoint.Property(EndpointProperty.Host))
        .WithEnvironment("Smtp__Port", smtpEndpoint.Property(EndpointProperty.Port));
}

// Pass optional CLI args to the worker (e.g. "run" or "check-config" set via AppHost launch profile)
var workerArgs = builder.Configuration["WorkerArgs"];
if (!string.IsNullOrWhiteSpace(workerArgs))
    worker.WithArgs(workerArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));

builder.Build().Run();
