# Talking Points Summary

A .NET 10 Worker Service that automatically fetches school messages

## Architecture

```
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

## Environment Variables

| Variable | Description | Default |
|---|---|---|
| `CONNECTION_STRING` | PostgreSQL connection string | `Host=localhost;Database=talkingpoints;...` |
| `ANTHROPIC_API_KEY` | Anthropic API key for Claude | *(required)* |
| `BROWSERLESS_URL` | Browserless service URL | `http://browserless:3000` |
| `SMTP_HOST` | SMTP server hostname | `smtp.gmail.com` |
| `SMTP_PORT` | SMTP server port | `587` |
| `SMTP_USERNAME` | SMTP login username | *(required)* |
| `SMTP_PASSWORD` | SMTP login password (app password) | *(required)* |
| `SMTP_FROM` | Sender email address | *(required)* |
| `SCHEDULE_DAY` | Day of week to run (0=Sun, 1=Mon, ...) | `1` (Monday) |
| `SCHEDULE_HOUR` | Hour to run (UTC, 24h) | `8` |

## Quick Start

### 1. Clone and configure

```bash
git clone <repo-url>
cd TalkingPointsSummary
cp .env.example .env
# Edit .env with your actual credentials
```

### 2. Build and run with Docker

```bash
docker compose up -d --build
```

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

```bash
# Add a parent
docker exec talking-points-summary \
  dotnet TalkingPointsSummary.dll add-parent \
  --name "Froehlich" \
  --token "your-talkingpoints-x-token" \
  --contact-id "your-talkingpoints-x-contactid" \
  --emails "parent1@email.com;parent2@email.com"

# Add children
docker exec talking-points-summary \
  dotnet TalkingPointsSummary.dll add-child \
  --parent-id 1 \
  --name "Clara" \
  --school "James Baldwin Elementary" \
  --grade 0 \
  --year 2025 \
  --emoji "📚"

docker exec talking-points-summary \
  dotnet TalkingPointsSummary.dll add-child \
  --parent-id 1 \
  --name "Nolan" \
  --school "Cascadia Elementary" \
  --grade 3 \
  --year 2025 \
  --emoji "🎓"
```

### 4. Test with a manual run

```bash
docker exec talking-points-summary \
  dotnet TalkingPointsSummary.dll run
```

### 5. Verify

Check your email for the weekly summary. The service will now automatically run every Monday at 8 AM UTC.

## Local Development

### F5 Debugging in Visual Studio

The solution uses [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/get-started/aspire-overview) to orchestrate local dependencies. Set `TalkingPointsSummary.AppHost` as the startup project and press **F5**:

- PostgreSQL starts automatically in Docker, waits until healthy
- Worker Service starts with the connection string injected
- Aspire dashboard opens showing logs and resource health
- Stopping the debugger shuts the container down

**On a machine with external PostgreSQL:** Set `ManagePostgres: false` and supply a connection string in the AppHost via user secrets — no code changes needed. See [docs/F5-DEBUGGING.md](docs/F5-DEBUGGING.md).

**With an external Browserless service:** Set `ManageBrowserless: false` and configure `BrowserlessUrl` in the AppHost. The worker will use that external endpoint instead of the Aspire-managed container.

**On a machine with external Browserless:** Set `ManageBrowserless: false` and supply `BrowserlessUrl` in the AppHost via user secrets or `appsettings.Development.json`. See [docs/F5-DEBUGGING.md](docs/F5-DEBUGGING.md).

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

# Run locally (requires Postgres + env vars)
cd src/TalkingPointsSummary
dotnet run

# Stop PostgreSQL
docker-compose down
```

## Database Schema

The app uses EF Core with code-first migrations, auto-applied on startup.

| Table | Purpose |
|---|---|
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
