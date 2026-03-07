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
    "postgres": "Host=myserver;Database=talkingpoints;Username=postgres;Password=secret"
  }
}
```

To use user secrets:
```bash
cd src/TalkingPointsSummary.AppHost
dotnet user-secrets set ManagePostgres false
dotnet user-secrets set ConnectionStrings:postgres "Host=...;Database=talkingpoints;..."
```

The Worker Service code is unaffected in either case.

## Running the Worker Directly (Without Aspire)

If you want to run or debug the `TalkingPointsSummary` worker project directly — without starting the AppHost — the connection string is read from `CONNECTION_STRING` in `src/TalkingPointsSummary/Properties/launchSettings.json`:

```json
"CONNECTION_STRING": "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres"
```

Update that value to match your local PostgreSQL instance. This file is committed with the default localhost credentials, so adjust it for your machine without committing the change if your credentials differ.

Alternatively, use `dotnet user-secrets` on the worker project so no file change is needed:

```bash
cd src/TalkingPointsSummary
dotnet user-secrets set CONNECTION_STRING "Host=myserver;Database=talkingpoints;Username=postgres;Password=secret"
```

> **Note:** User secrets take precedence over `launchSettings.json` environment variables when running via `dotnet run` or Visual Studio.
