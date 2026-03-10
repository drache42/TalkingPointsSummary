# F5 debugging with Aspire

Use `TalkingPointsSummary.AppHost` when you want Visual Studio to start the worker, admin UI, and local dependencies together. The AppHost manages PostgreSQL, Browserless, and Mailpit by default and wires the worker and admin together for local debugging.

## Prerequisites

- Docker Desktop installed and running
- The repository restored and buildable from the repo root

## What F5 starts by default

The AppHost reads these defaults from `src/TalkingPointsSummary.AppHost/appsettings.json`:

```json
{
  "ManagePostgres": true,
  "ManageBrowserless": true,
  "ManageMailpit": true
}
```

With those defaults in place, F5 does all of the following:

- Starts PostgreSQL 15 in Docker and injects `ConnectionStrings__TalkingPoints`
- Starts Browserless and injects `Browserless__BaseUrl`
- Starts Mailpit and injects `Smtp__Host` and `Smtp__Port`
- Starts the worker with `ASPNETCORE_URLS=http://127.0.0.1:5101`
- Starts the admin UI with `WorkerDebugBaseUrl=http://127.0.0.1:5101/`
- Forces `DebugFeatures__Enabled=true` for both the worker and the admin UI

When F5 is running, the admin debug page is available at `/debug` and the worker accepts `POST /debug/pipeline/run-now`.

## Launch profiles

`TalkingPointsSummary.AppHost` includes these useful launch profiles:

- `http`: start the full AppHost stack
- `https`: start the full AppHost stack with HTTPS endpoints for the dashboard profile
- `http (run now)`: pass `WorkerArgs=run` to the worker so it executes the CLI `run` command
- `http (check-config)`: pass `WorkerArgs=check-config` to the worker so it executes the validator command

The `WorkerArgs` setting is split on spaces and forwarded directly to the worker process.

## Standard F5 workflow

1. Open the solution in Visual Studio.
2. Set `TalkingPointsSummary.AppHost` as the startup project.
3. Select the launch profile you want.
4. Press F5.

Expected result:

- Aspire opens its dashboard for logs and resource health.
- The worker applies EF Core migrations on startup.
- The admin UI starts against the same database.
- The admin debug page can trigger the worker debug endpoint.

## Using external PostgreSQL

If you already have PostgreSQL outside Docker, disable the managed container in the AppHost and provide the connection string there.

Example AppHost user secrets:

```bash
cd src/TalkingPointsSummary.AppHost
dotnet user-secrets set "ManagePostgres" "false"
dotnet user-secrets set "ConnectionStrings:TalkingPoints" "Host=myserver;Database=talkingpoints;Username=postgres;Password=secret"
```

You can store the same settings in `src/TalkingPointsSummary.AppHost/appsettings.Development.json` if you prefer file-based local config.

## Using external Browserless

If Browserless runs outside Aspire, disable the managed container and provide `Browserless:BaseUrl` in the AppHost configuration.

```bash
cd src/TalkingPointsSummary.AppHost
dotnet user-secrets set "ManageBrowserless" "false"
dotnet user-secrets set "Browserless:BaseUrl" "http://127.0.0.1:50660"
```

When `ManageBrowserless=false`, the AppHost throws on startup unless `Browserless:BaseUrl` is configured.

## Using external Mailpit or SMTP

`ManageMailpit` controls only the AppHost-managed Mailpit container.

```bash
cd src/TalkingPointsSummary.AppHost
dotnet user-secrets set "ManageMailpit" "false"
```

When `ManageMailpit=false`, the AppHost stops injecting `Smtp__Host` and `Smtp__Port`. The worker then falls back to its own configuration sources:

- `src/TalkingPointsSummary/appsettings.json`
- `src/TalkingPointsSummary/appsettings.Development.json`
- `src/TalkingPointsSummary/appsettings.Local.json`
- worker user secrets
- worker environment variables

In Development, the worker already defaults to `localhost:1025`. If you want a different SMTP server, set worker user secrets or environment variables such as `Smtp:Host`, `Smtp:Port`, `Smtp:Username`, `Smtp:Password`, and `Smtp:FromEmail`.

## Running the worker directly without AppHost

If you want to debug only the worker project, configure worker secrets directly:

```bash
cd src/TalkingPointsSummary
dotnet user-secrets set "ConnectionStrings:TalkingPoints" "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres"
dotnet user-secrets set "Anthropic:ApiKey" "your-anthropic-key"
dotnet user-secrets set "Smtp:FromEmail" "you@example.com"
dotnet user-secrets set "DebugFeatures:Enabled" "true"
```

With `DebugFeatures:Enabled=true` and no CLI arguments, the worker runs the debug web host instead of the plain background host. In Development, if `ASPNETCORE_URLS` is not already set, it binds to `http://127.0.0.1:5101`.

## Running the admin directly without AppHost

If you want to debug only the admin UI, configure it with the database connection string and the worker debug base URL:

```bash
cd src/TalkingPointsSummary.Admin
dotnet user-secrets set "ConnectionStrings:TalkingPoints" "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres"
dotnet user-secrets set "WorkerDebugBaseUrl" "http://127.0.0.1:5101/"
dotnet user-secrets set "DebugFeatures:Enabled" "true"
```

If `WorkerDebugBaseUrl` is missing, the admin debug page still renders when debug features are enabled, but the trigger client reports that the worker URL is not configured.
