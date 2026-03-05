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
