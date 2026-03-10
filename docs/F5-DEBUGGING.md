# F5 Debugging with PostgreSQL

The solution uses [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/get-started/aspire-overview) to orchestrate local dependencies. Pressing F5 launches the `TalkingPointsSummary.AppHost` project, which starts PostgreSQL in Docker and then starts the Worker Service with the correct connection string injected automatically.

## Prerequisites

- Docker Desktop installed and running
- `postgres:15-alpine` image pulled: `docker pull postgres:15-alpine`

## Usage

Set `TalkingPointsSummary.AppHost` as the startup project in Visual Studio, then press **F5**.

- PostgreSQL starts in Docker, waits until healthy, then the Worker Service starts
- The Aspire dashboard opens in your browser showing logs and resource health
- Stopping the debugger shuts the container down

## Using an External PostgreSQL (Different Machine Setup)

If you have your own PostgreSQL instance (local install, remote server, cloud), override two settings in the AppHost — either via user secrets (recommended) or `appsettings.Development.json`:

```json
{
  "ManagePostgres": false,
  "ConnectionStrings": {
    "TalkingPoints": "Host=myserver;Database=talkingpoints;Username=postgres;Password=secret"
  }
}
```

To use user secrets:
```bash
cd src/TalkingPointsSummary.AppHost
dotnet user-secrets set ManagePostgres false
dotnet user-secrets set ConnectionStrings:TalkingPoints "Host=...;Database=talkingpoints;..."
```

The Worker Service code is unaffected in either case.

## Using an External Browserless Service

If Browserless is running outside Aspire, disable the managed container and provide the external base URL in the AppHost:

```json
{
  "ManageBrowserless": false,
  "Browserless": {
    "BaseUrl": "http://127.0.0.1:50660"
  }
}
```

To use user secrets:

```bash
cd src/TalkingPointsSummary.AppHost
dotnet user-secrets set ManageBrowserless false
dotnet user-secrets set Browserless:BaseUrl "http://127.0.0.1:50660"
```

When `ManageBrowserless` is `false`, the AppHost now requires `Browserless:BaseUrl` to be set and injects it into the worker as `Browserless__BaseUrl`.

## Running the Worker Directly (Without Aspire)

If you want to run or debug the `TalkingPointsSummary` worker project directly — without starting the AppHost — configure the worker project with user secrets:

```bash
cd src/TalkingPointsSummary
dotnet user-secrets set ConnectionStrings:TalkingPoints "Host=myserver;Database=talkingpoints;Username=postgres;Password=secret"
dotnet user-secrets set Anthropic:ApiKey "your-anthropic-key"
dotnet user-secrets set Smtp:FromEmail "you@example.com"
```

For Mailpit-style local SMTP, `src/TalkingPointsSummary/appsettings.Development.json` already defaults to `Smtp:Host = localhost` and `Smtp:Port = 1025`.

> **Note:** User secrets take precedence over appsettings when running via `dotnet run` or Visual Studio.

## First-Time Setup: Generating Migrations

If this is a fresh clone with no migration files present, generate them once before running the app.

**No database or Aspire session required.** The project includes `AppDbContextFactory`, which the EF tooling uses directly to build the `DbContext` from your model classes — it never executes `Program.cs` or connects to the database.

```bash
dotnet ef migrations add InitialCreate --project src/TalkingPointsSummary
```

Commit the generated files under `src/TalkingPointsSummary/Migrations/`. After that, `MigrateAsync()` in `Program.cs` applies the migration automatically on every startup in every environment (local, Docker, production). You never need to run `dotnet ef database update` or any migration command in production.
