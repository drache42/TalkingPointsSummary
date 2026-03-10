# Talking Points Summary

A .NET 10 weekly-digest system with a worker service, a Blazor admin UI, and an Aspire AppHost for local orchestration.

## Architecture

```text
┌─────────────────────────────────────────────────────────────┐
│                    Weekly Pipeline (Monday 8 AM)            │
│                                                             │
│  Fetch API → Dedup → AI Categorize → Scrape Newsletters    │
│       → Store News → AI Summarize → Email → Archive        │
│                                                             │
│  Repeated for each active parent                            │
└─────────────────────────────────────────────────────────────┘
         │                │                    │
    TalkingPoints     Anthropic API      Browserless
       API          (Haiku + Sonnet)    (headless Chrome)
         │                │                    │
         └────────────────┴────────────────────┘
                          │
                     PostgreSQL
```

## Prerequisites

- **Docker** (for containerized deployment)
- **PostgreSQL** (external database)
- **Browserless** (external headless Chrome service)
- **Anthropic API key** (for Claude Haiku + Sonnet)
- **Gmail SMTP credentials** (or other SMTP provider)
- **TalkingPoints account** (parent credentials)

## Repository Shape

- `src/TalkingPointsSummary`: worker service and CLI
- `src/TalkingPointsSummary.Admin`: Blazor Server admin UI
- `src/TalkingPointsSummary.AppHost`: .NET Aspire local-development orchestrator

The admin UI and debug tooling are intentional open-source development features. They exist to help contributors inspect data, validate configuration, and manually exercise the pipeline during local development.

## Application Configuration

The worker uses standard hierarchical .NET configuration sections.

| Setting | Environment Variable Equivalent | Description |
| --- | --- | --- |
| `ConnectionStrings:TalkingPoints` | `ConnectionStrings__TalkingPoints` | PostgreSQL connection string |
| `Anthropic:ApiKey` | `Anthropic__ApiKey` | Anthropic API key for Claude |
| `Browserless:BaseUrl` | `Browserless__BaseUrl` | Browserless base URL |
| `DebugFeatures:Enabled` | `DebugFeatures__Enabled` | Enables the public development-only debug tools, including the Admin Debug page and worker debug trigger endpoint |
| `NewsletterScrapingSecurity:Enabled` | `NewsletterScrapingSecurity__Enabled` | Enables newsletter URL validation before Browserless fetches a page |
| `NewsletterScrapingSecurity:RequireHttps` | `NewsletterScrapingSecurity__RequireHttps` | Requires HTTPS for newsletter URLs except for explicitly allowed HTTP hosts |
| `TalkingPointsApi:MaxPagesPerRun` | `TalkingPointsApi__MaxPagesPerRun` | Safety cap on how many TalkingPoints feed pages one pipeline run may fetch |
| `Smtp:Host` | `Smtp__Host` | SMTP server hostname |
| `Smtp:Port` | `Smtp__Port` | SMTP server port |
| `Smtp:Username` | `Smtp__Username` | SMTP login username |
| `Smtp:Password` | `Smtp__Password` | SMTP login password |
| `Smtp:FromEmail` | `Smtp__FromEmail` | Sender email address |
| `PipelineSchedule:DayOfWeek` | `PipelineSchedule__DayOfWeek` | Day of week to run (0=Sun, 1=Mon, ...) |
| `PipelineSchedule:Hour` | `PipelineSchedule__Hour` | Hour to run (UTC, 24h) |

`appsettings.Local.json` is still loaded if present and should use the same nested JSON structure as `appsettings.json`. For local development, user secrets are the preferred place for secrets.

If you prefer environment variables, use the section-based names shown above. [.env.example](.env.example) includes both Docker Compose overrides and direct worker environment variable examples.

## Quick Start

Choose one local workflow:

- Docker Compose: fastest way to run the full containerized stack
- Aspire AppHost: best option for F5 debugging and local dependency orchestration
- Worker only: useful when you already have PostgreSQL and Browserless running elsewhere

### 1. Clone and configure

```bash
git clone <repo-url>
cd TalkingPointsSummary
```

For local development, configure the worker with user secrets:

```bash
cd src/TalkingPointsSummary
dotnet user-secrets set "ConnectionStrings:TalkingPoints" "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres"
dotnet user-secrets set "Anthropic:ApiKey" "your-anthropic-key"
dotnet user-secrets set "Smtp:FromEmail" "you@example.com"
```

Set `Smtp:Username` and `Smtp:Password` only if your SMTP server requires authentication. `appsettings.Development.json` already defaults to `localhost:1025` for Mailpit-style local SMTP.

Set `DebugFeatures__Enabled=true` when you want the public development-only debug tools available. That enables the Admin Debug page and the worker debug trigger endpoint. Leave it unset or `false` to disable them.

Newsletter scraping now validates AI-supplied URLs before sending them to Browserless. By default only public HTTPS URLs are allowed. Development and test environments can opt specific hosts into `AllowedHosts` and `AllowHttpHosts` when Browserless must scrape host-served content such as `host.docker.internal`.

TalkingPoints fetching now stops when it reaches the newest saved message ID, when feed timestamps move older than the newest saved message timestamp, or when `TalkingPointsApi:MaxPagesPerRun` is reached. The default page cap is `3` to prevent large historical fetches from hammering TalkingPoints in a single run.

Outgoing HTTP calls now use the Microsoft resilience handler package with exponential backoff and jitter for transient failures on TalkingPoints, Anthropic, and Browserless requests. The retry policy is configured centrally in the worker's `HttpClient` registrations.

### 2. Build and run with Docker

```bash
docker compose up -d --build
```

This starts:

- the worker service
- the admin UI at `http://localhost:5100`
- Mailpit at `http://localhost:8025`
- Browserless at `http://localhost:3000`
- PostgreSQL at `localhost:5432`

If `DEBUG_FEATURES_ENABLED=true`, the admin UI also exposes the development Debug page and the worker accepts manual debug-trigger requests.

`docker-compose.yml` defaults `DebugFeatures__Enabled` to `false`. Set `DEBUG_FEATURES_ENABLED=true` before starting the stack if you want the debug tools enabled.

The worker writes rolling log files to `/app/logs` inside the container. By default, `docker-compose.yml` bind-mounts that to `./docker-data/logs` on the host:

```yaml
volumes:
  - ${TPS_LOGS_PATH:-./docker-data/logs}:/app/logs
```

If you want the same pattern as your other services, set `TPS_LOGS_PATH` to a host folder before starting the stack, for example:

```bash
TPS_LOGS_PATH=/volume1/docker-volumes/talking-points-summary/logs docker compose up -d --build
```

Or hardcode the host path directly in `docker-compose.yml`:

```yaml
volumes:
  - /volume1/docker-volumes/talking-points-summary/logs:/app/logs
```

### 3. Add a parent and children

#### Getting TalkingPoints credentials

For most contributors, this is the hardest setup step. You need two values from your own TalkingPoints session:

- `x-token`
- `x-contactid`

Walkthrough:

1. Open `https://families.talkingpts.org/login` in your browser.
2. Sign in with the phone number for the parent account you want this app to summarize.
3. Enter the verification code TalkingPoints sends you.
4. After you are signed in, open browser developer tools. `F12` works in most desktop browsers.
5. With developer tools open, go to the `Network` tab.
6. Refresh the page so the network list repopulates while dev tools are already open.
7. Click almost any TalkingPoints API request in the left-hand request list.
8. In the request details, open `Headers` and look under `Request Headers`.
9. Copy the values for `X-Contactid` and `X-Token`.

![TalkingPoints request headers in browser dev tools](docs/images/talkingpoints-devtools-request-headers.png)

Tips:

- If you do not see useful requests immediately, refresh again after you are fully signed in.
- The exact request name is not important. Any authenticated TalkingPoints API request that includes those headers is enough.
- Treat both values like credentials. Do not commit them to the repository.

Use those values with the `add-parent` command:

```bash
# Add a parent
docker exec talking-points-summary \
  dotnet TalkingPointsSummary.dll add-parent \
  --name "ExampleFamily" \
  --token "your-talkingpoints-x-token" \
  --contact-id "your-talkingpoints-x-contactid" \
  --emails "parent1@email.com;parent2@email.com"

# Add children
docker exec talking-points-summary \
  dotnet TalkingPointsSummary.dll add-child \
  --parent-id 1 \
  --name "StudentOne" \
  --school "Sample Elementary" \
  --grade 0 \
  --emoji "📚"

docker exec talking-points-summary \
  dotnet TalkingPointsSummary.dll add-child \
  --parent-id 1 \
  --name "StudentTwo" \
  --school "Demo Elementary" \
  --grade 3 \
  --emoji "🎓"
```

### 4. Test with a manual run

Before running the pipeline manually, you can validate configuration and external connectivity:

```bash
docker exec talking-points-summary \
  dotnet TalkingPointsSummary.dll check-config
```

```bash
docker exec talking-points-summary \
  dotnet TalkingPointsSummary.dll run
```

### 5. Verify

Use the admin UI at `http://localhost:5100` to inspect configured parents and children, and use Mailpit at `http://localhost:8025` to inspect delivered email locally. When debug features are enabled, the admin UI also exposes a Debug page for contributor workflows such as triggering manual runs. The service will automatically run every Monday at 8 AM UTC.

## Local Development

### F5 Debugging in Visual Studio

The solution uses [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/get-started/aspire-overview) to orchestrate local dependencies. Set `TalkingPointsSummary.AppHost` as the startup project and press **F5**:

- PostgreSQL starts automatically in Docker, waits until healthy
- Browserless and Mailpit start automatically when managed locally
- Worker Service starts with the connection string injected
- Admin UI starts with the same database reference
- Aspire dashboard opens showing logs and resource health
- Stopping the debugger shuts the container down

**On a machine with external PostgreSQL:** Set `ManagePostgres: false` and supply `ConnectionStrings:TalkingPoints` in the AppHost via user secrets — no code changes needed. See [docs/F5-DEBUGGING.md](docs/F5-DEBUGGING.md).

**With an external Browserless service:** Set `ManageBrowserless: false` and configure `Browserless:BaseUrl` in the AppHost. The worker will use that external endpoint instead of the Aspire-managed container.

**On a machine with external Browserless:** Set `ManageBrowserless: false` and supply `Browserless:BaseUrl` in the AppHost via user secrets or `appsettings.Development.json`. See [docs/F5-DEBUGGING.md](docs/F5-DEBUGGING.md).

**Prerequisite:** Docker Desktop running with `postgres:15-alpine` pulled.

### Manual Development

```bash
# Start PostgreSQL manually
docker-compose up -d postgres

# Restore and build
dotnet restore
dotnet build

# Generate EF Core migration files (first time only — no database or Aspire required)
# Once generated, commit them. The app applies migrations automatically on startup.
dotnet ef migrations add InitialCreate --project src/TalkingPointsSummary

# Run tests
dotnet test

# Run locally (requires Postgres + configured secrets)
cd src/TalkingPointsSummary
dotnet run

# Stop PostgreSQL
docker-compose down
```

For direct local runs, configure the worker through user secrets or environment variables using the section-based names shown above.

The admin UI can also be run directly from `src/TalkingPointsSummary.Admin` once the database is available.

## Database Schema

The app uses EF Core with code-first migrations, auto-applied on startup.

| Table | Purpose |
| --- | --- |
| `Parents` | Registered parent accounts with TalkingPoints credentials |
| `Children` | Children linked to parents, with school and grade info |
| `Messages` | Raw messages fetched from TalkingPoints API |
| `NewsItems` | Categorized news (direct messages or scraped newsletters) |
| `Summaries` | Archived weekly email summaries |

## How It Works

1. **Fetch**: Pulls the latest 20 messages from the TalkingPoints API for each parent
2. **Dedup**: Compares against stored messages by external ID, saves only new ones
3. **Categorize**: Claude Haiku analyzes each unprocessed message to determine:
   - Does it contain a newsletter URL? → scrape it
   - Is it newsworthy by itself? → save directly
4. **Scrape**: For newsletter URLs, Browserless extracts the full text content
5. **Store**: Saves categorized news items to the database
6. **Summarize**: Claude Sonnet generates a warm, scannable weekly briefing, deduplicating against the last 6 weeks of summaries
7. **Email**: Converts Markdown to HTML and sends via SMTP
8. **Archive**: Saves the summary for future deduplication

## Grade Calculation

Children advance one grade every September 1st. Configure the starting grade and year when adding a child:

- Grade 0 = Kindergarten
- Grade 1 = 1st Grade, etc.

The system automatically calculates the current grade based on today's date.
