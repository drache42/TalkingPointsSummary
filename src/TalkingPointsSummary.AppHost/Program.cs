using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<ProjectResource> worker;

if (builder.Configuration.GetValue<bool>("ManagePostgres", true))
{
    // Aspire manages a local PostgreSQL container (default for Docker-based dev machines)
    var postgresPassword = builder.AddParameter("postgres-password", secret: true);

    var postgres = builder.AddPostgres("postgres", password: postgresPassword)
        .WithImage("postgres", "15-alpine")
        .WithDataVolume("talkingpoints-postgres-data");

    var db = postgres.AddDatabase("talkingpoints");

    worker = builder.AddProject<Projects.TalkingPointsSummary>("worker")
        .WithReference(db)
        .WaitFor(db)
        .WithEnvironment("CONNECTION_STRING", db);
}
else
{
    // External PostgreSQL — supply connection string via appsettings.json or user secrets:
    //   "ConnectionStrings": { "postgres": "Host=...;Database=...;Username=...;Password=..." }
    var postgres = builder.AddConnectionString("postgres");

    worker = builder.AddProject<Projects.TalkingPointsSummary>("worker")
        .WithEnvironment("CONNECTION_STRING", postgres);
}

if (builder.Configuration.GetValue<bool>("ManageBrowserless", true))
{
    // Aspire manages a local Browserless container (default for Docker-based dev machines)
    var browserless = builder.AddContainer("browserless", "browserless/chrome")
        .WithHttpEndpoint(port: 3000, targetPort: 3000)
        .WithEnvironment("MAX_CONCURRENT_SESSIONS", "2")
        .WithEnvironment("MAX_QUEUE_LENGTH", "5");

    worker
        .WaitFor(browserless)
        .WithEnvironment("BROWSERLESS_URL", browserless.GetEndpoint("http"));
}

builder.Build().Run();
