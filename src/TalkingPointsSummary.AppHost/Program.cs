using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

if (builder.Configuration.GetValue<bool>("ManagePostgres", true))
{
    // Aspire manages a local PostgreSQL container (default for Docker-based dev machines)
    var postgresPassword = builder.AddParameter("postgres-password", secret: true);

    var postgres = builder.AddPostgres("postgres", password: postgresPassword)
        .WithImage("postgres", "15-alpine")
        .WithDataVolume("talkingpoints-postgres-data");

    var db = postgres.AddDatabase("talkingpoints");

    builder.AddProject<Projects.TalkingPointsSummary>("worker")
        .WithReference(db)
        .WaitFor(db)
        .WithEnvironment("CONNECTION_STRING", db);
}
else
{
    // External PostgreSQL — supply connection string via appsettings.json or user secrets:
    //   "ConnectionStrings": { "postgres": "Host=...;Database=...;Username=...;Password=..." }
    var postgres = builder.AddConnectionString("postgres");

    builder.AddProject<Projects.TalkingPointsSummary>("worker")
        .WithEnvironment("CONNECTION_STRING", postgres);
}

builder.Build().Run();
