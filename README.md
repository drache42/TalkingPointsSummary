# Talking Points Summary

Talking Points Summary turns school messages from TalkingPoints into weekly email digests for parents. The repository includes a .NET 10 worker, a Blazor Server admin UI, and a .NET Aspire AppHost for local development.

## What this is

The worker fetches messages from the TalkingPoints parent feed, stores only new messages, uses Anthropic models to classify and summarize school updates, scrapes newsletter links through Browserless when needed, tracks the dated events it finds, reviews the draft digest before it goes out, and emails the result through SMTP.

## Who this is for

- Developers who want to run or contribute to the worker locally
- Parents or operators who manage TalkingPoints credentials and delivery settings
- Contributors who need a local admin surface to inspect data and trigger test runs

## Repository shape

- `src/TalkingPointsSummary`: worker service and CLI
- `src/TalkingPointsSummary.Admin`: Blazor Server admin UI
- `src/TalkingPointsSummary.AppHost`: .NET Aspire local orchestrator
- `src/TalkingPointsSummary.Core`: shared models, EF Core context, and parent/child services
- `tests`: unit and integration tests

## Quick start

Choose one workflow:

- Docker Compose: run the full stack in containers
- Aspire AppHost: run the stack under Visual Studio F5 with managed dependencies
- Direct project run: run the worker and admin against services you manage yourself

### 1. Clone the repository

```bash
git clone <repo-url>
cd TalkingPointsSummary
```

### 2. Configure secrets and local settings

For Docker Compose, start from the checked-in example file:

```bash
cp .env.example .env
```

Edit `.env` and set at least:

- `AI_GATEWAY_API_KEY`
- `AI_GATEWAY_BASE_URL`
- `SMTP_FROM`

Compose passes those two AI values through as `Ai__Anthropic__ApiKey` and `Ai__Anthropic__BaseUrl`. AI traffic is routed through a self-hosted LiteLLM gateway that speaks the Anthropic messages API, so the key is a gateway virtual key rather than a raw Anthropic key, and model access is granted per key. Point `AI_GATEWAY_BASE_URL` at `https://api.anthropic.com` and supply a real Anthropic key to talk to Anthropic directly instead.

For direct worker runs, set user secrets on the worker project:

```bash
cd src/TalkingPointsSummary
dotnet user-secrets set "ConnectionStrings:TalkingPoints" "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres"
dotnet user-secrets set "Ai:Anthropic:ApiKey" "your-ai-gateway-virtual-key"
dotnet user-secrets set "Ai:Anthropic:BaseUrl" "http://192.168.1.2:4100/anthropic"
dotnet user-secrets set "Smtp:FromEmail" "you@example.com"
```

Set `Ai:Anthropic:BaseUrl` whenever the key is a gateway key. Left at its `https://api.anthropic.com` default, a gateway key is sent to Anthropic and rejected with 401.

Set `Smtp:Username` and `Smtp:Password` only when your SMTP server requires authentication. In Development, the worker already overrides SMTP defaults to `localhost:1025`, which matches Mailpit.

### 3. Start the stack

#### Docker Compose

```bash
docker compose -f infra/docker-compose.yml --env-file .env up -d --build
```

That starts:

- the worker container `talking-points-summary`
- the admin UI at `http://localhost:5100`
- Browserless at `http://localhost:3000`
- Mailpit at `http://localhost:8025`
- PostgreSQL at `localhost:5432`

The Compose worker is not published on a host port. The admin container talks to it internally at `http://app:8080/`.
The Compose stack stores bind-mounted runtime state under `runtime-data/` by default. That includes the admin DataProtection key ring at `runtime-data/admin-data-protection-keys`, so protected state survives container restarts.

#### Aspire AppHost

Set `TalkingPointsSummary.AppHost` as the startup project and press F5. The AppHost manages PostgreSQL, Browserless, and Mailpit by default, injects `ConnectionStrings__TalkingPoints`, forces `DebugFeatures__Enabled=true` for the worker and admin, and points the admin debug client at `http://127.0.0.1:5101/`.

See `docs/F5-DEBUGGING.md` for the dependency flags and launch profile details.

### 4. Register a parent and children

#### Getting TalkingPoints credentials

You need two headers from an authenticated TalkingPoints parent session:

- `x-token`
- `x-contactid`

To get them:

1. Open `https://families.talkingpts.org/login`.
2. Sign in with the parent account you want to summarize.
3. Open your browser developer tools.
4. Open the `Network` tab and refresh the page.
5. Select any authenticated TalkingPoints API request.
6. Open `Headers` and copy `x-token` and `x-contactid` from `Request Headers`.

![TalkingPoints request headers in browser dev tools](docs/images/talkingpoints-devtools-request-headers.png)

Treat both values as secrets.

You can register parents and children in either interface:

- Admin UI: open `http://localhost:5100/parents`, select `Add Parent`, then open the parent record and select `Add Child`
- CLI: use the commands below from the worker container

The admin UI uses the same parent and child services as the CLI. The parent form also includes inline guidance for finding `x-token` and `x-contactid` in your browser dev tools.

CLI example:

```bash
docker exec talking-points-summary \
  dotnet TalkingPointsSummary.dll add-parent \
  --name "ExampleFamily" \
  --token "your-talkingpoints-x-token" \
  --contact-id "your-talkingpoints-x-contactid" \
  --emails "parent1@example.com;parent2@example.com"

docker exec talking-points-summary \
  dotnet TalkingPointsSummary.dll add-child \
  --parent-id 1 \
  --name "StudentOne" \
  --school "Sample Elementary" \
  --grade 0 \
  --emoji "📚"
```

### 5. Verify configuration and run the pipeline

Check configuration and connectivity:

```bash
docker exec talking-points-summary \
  dotnet TalkingPointsSummary.dll check-config
```

Run the pipeline manually for all active parents:

```bash
docker exec talking-points-summary \
  dotnet TalkingPointsSummary.dll run
```

Use the admin UI at `http://localhost:5100` to add and manage parents and children, inspect stored data, and, when debug features are enabled, trigger manual runs from the debug page. Use Mailpit at `http://localhost:8025` to inspect delivered email.

## Configuration

The worker loads configuration in this order:

1. `src/TalkingPointsSummary/appsettings.json`
2. `src/TalkingPointsSummary/appsettings.{Environment}.json`
3. `src/TalkingPointsSummary/appsettings.Local.json` if present
4. Development user secrets for `src/TalkingPointsSummary`
5. Environment variables

### Worker settings

| Setting | Environment variable | Default | Required | Notes |
| --- | --- | --- | --- | --- |
| `ConnectionStrings:TalkingPoints` | `ConnectionStrings__TalkingPoints` | empty | Yes | PostgreSQL connection string |
| `Ai:Provider` | `Ai__Provider` | `Anthropic` | Yes | Only `Anthropic` is supported |
| `Ai:Anthropic:ApiKey` | `Ai__Anthropic__ApiKey` | empty | Yes | Anthropic key, or a gateway virtual key when `BaseUrl` points at a gateway. The legacy `Anthropic:ApiKey` key still works but logs a deprecation warning |
| `Ai:Anthropic:BaseUrl` | `Ai__Anthropic__BaseUrl` | `https://api.anthropic.com` | No | Base URL for the Anthropic messages API. Set this to the LiteLLM gateway endpoint when using a gateway key |
| `Ai:Anthropic:ApiVersion` | `Ai__Anthropic__ApiVersion` | `2023-06-01` | No | Value of the `anthropic-version` header |
| `Ai:Profiles:<Profile>:ModelId` | `Ai__Profiles__<Profile>__ModelId` | see below | Yes | Model per profile. Profiles are `Categorization`, `Summarization`, `Critique`, and `Validation` |
| `Ai:Profiles:<Profile>:MaxTokens` | `Ai__Profiles__<Profile>__MaxTokens` | see below | No | Output ceiling. Thinking tokens count against it, so thinking profiles need a larger value |
| `Ai:Profiles:<Profile>:Thinking` | `Ai__Profiles__<Profile>__Thinking` | see below | No | `none`, `adaptive`, or `budget`. Claude 5 and later accept only `adaptive`; Claude 4.5 and earlier accept only `budget`. A mismatch fails at startup rather than on every call |
| `Ai:Profiles:<Profile>:Effort` | `Ai__Profiles__<Profile>__Effort` | see below | No | `low`, `medium`, `high`, `xhigh`, or `max`. Sent only with `adaptive` thinking |
| `Ai:Profiles:<Profile>:ThinkingBudgetTokens` | `Ai__Profiles__<Profile>__ThinkingBudgetTokens` | `0` | No | Thinking budget, used only with `budget` thinking. Must be at least 1024 and below `MaxTokens` |
| `Browserless:BaseUrl` | `Browserless__BaseUrl` | `http://browserless:3000` | Yes | Must be an absolute URL |
| `DebugFeatures:Enabled` | `DebugFeatures__Enabled` | `false` | No | When `true`, the worker runs the debug web host and exposes `POST /debug/pipeline/run-now` |
| `NewsletterScrapingSecurity:Enabled` | `NewsletterScrapingSecurity__Enabled` | `true` | No | Enables URL validation before Browserless is called |
| `NewsletterScrapingSecurity:RequireHttps` | `NewsletterScrapingSecurity__RequireHttps` | `true` | No | Blocks non-HTTPS newsletter URLs unless explicitly allowed |
| `NewsletterScrapingSecurity:AllowedHosts` | `NewsletterScrapingSecurity__AllowedHosts__0`, etc. | empty list | No | Host allowlist for newsletter scraping |
| `NewsletterScrapingSecurity:AllowHttpHosts` | `NewsletterScrapingSecurity__AllowHttpHosts__0`, etc. | empty list | No | Hosts that may use `http` when scraping is enabled |
| `TalkingPointsApi:MaxPagesPerRun` | `TalkingPointsApi__MaxPagesPerRun` | `3` | No | Fetch limit; each page contains up to 20 messages |
| `Smtp:Host` | `Smtp__Host` | `smtp.gmail.com` in base config, `localhost` in Development | Yes | SMTP hostname |
| `Smtp:Port` | `Smtp__Port` | `587` in base config, `1025` in Development | Yes | SMTP port |
| `Smtp:Username` | `Smtp__Username` | empty | No | Must be paired with `Smtp:Password` |
| `Smtp:Password` | `Smtp__Password` | empty | No | Must be paired with `Smtp:Username`; for Gmail, use an App Password — see [docs/gmail-smtp.md](docs/gmail-smtp.md) |
| `Smtp:FromEmail` | `Smtp__FromEmail` | empty in base config, `dev@example.com` in Development | Yes | Sender address |
| `PipelineSchedule:DayOfWeek` | `PipelineSchedule__DayOfWeek` | `1` | No | Weekly schedule day, where `0=Sunday` and `1=Monday`. Interpreted in `PipelineSchedule:TimeZone` when set, otherwise UTC. |
| `PipelineSchedule:Hour` | `PipelineSchedule__Hour` | `8` | No | Weekly schedule hour in 24-hour time. Interpreted in `PipelineSchedule:TimeZone` when set, otherwise UTC. |
| `PipelineSchedule:TimeZone` | `PipelineSchedule__TimeZone` | `UTC` | No | Timezone for the schedule. Accepts IANA (`America/New_York`) or Windows (`Eastern Standard Time`) format. It is also the timezone every date in the digest is read in: event dates, "is this date still upcoming", and the date a digest is filed under. |

Profile defaults:

| Profile | ModelId | MaxTokens | Thinking | Effort | Used by |
| --- | --- | --- | --- | --- | --- |
| `Categorization` | `claude-haiku-4-5-20251001` | `1024` | `none` | unset | Message categorization and event extraction |
| `Summarization` | `claude-sonnet-5` | `32000` | `adaptive` | `high` | Digest generation and the revision pass |
| `Critique` | `claude-sonnet-5` | `8192` | `adaptive` | `high` | The AI review of a draft digest |
| `Validation` | `claude-haiku-4-5-20251001` | `1` | `none` | unset | The `check-config` credential probe |

### Admin settings

| Setting | Environment variable | Default | Required | Notes |
| --- | --- | --- | --- | --- |
| `WorkerDebugBaseUrl` | `WorkerDebugBaseUrl` | empty | No | Base URL used by the admin debug page to call the worker debug endpoint |
| `DataProtection:KeysDirectory` | `DataProtection__KeysDirectory` | empty outside containers; `/var/app/data-protection-keys` in containers | No | Set this to a persistent directory for any deployment where the admin app must keep cookies and antiforgery state across restarts |

For non-Compose deployments, mount a persistent directory into the admin container and set `DataProtection__KeysDirectory` to that in-container path.

### AppHost settings

The AppHost has its own configuration in `src/TalkingPointsSummary.AppHost/appsettings.json` and user secrets.

| Setting | Default | Notes |
| --- | --- | --- |
| `ManagePostgres` | `true` | Starts a local PostgreSQL 15 container and injects `ConnectionStrings__TalkingPoints` |
| `ManageBrowserless` | `true` | Starts a Browserless container and injects `Browserless__BaseUrl` |
| `ManageMailpit` | `true` | Starts Mailpit and injects `Smtp__Host` and `Smtp__Port` into the worker |
| `Browserless:BaseUrl` | `null` | Required only when `ManageBrowserless=false` |
| `WorkerArgs` | unset | Optional CLI arguments forwarded to the worker, for example `run` or `check-config` |

## How the pipeline works

1. The scheduler waits for the next configured day and hour (interpreted in `PipelineSchedule:TimeZone` when set, otherwise UTC) and runs immediately if the worker starts during that scheduled hour. If the scheduled evaluation is blocked by another active run or throws an error, it waits one minute and re-evaluates within that same scheduled hour; otherwise it advances to the next weekly occurrence. The default schedule is Monday at 08:00 UTC.
2. For each active parent, the worker fetches TalkingPoints feed pages of 20 messages each.
3. Fetching stops when it reaches the newest stored message ID, when it sees a message older than the newest stored timestamp, when a short or empty page is returned, or when `TalkingPointsApi:MaxPagesPerRun` is reached.
4. The deduplicator stores only messages whose `(ParentId, ExternalMessageId)` pair does not already exist in the database.
5. The categorizer sends each unprocessed message to Anthropic Haiku and decides whether the message contains a newsletter URL, whether the message text is itself newsworthy, and what short summary text to persist.
6. If a newsletter URL is present, the worker validates the URL, scrapes the page body through Browserless, and stores the scrape result as a `NewsItem`. If scraping fails or returns empty content, the worker falls back to storing the original message text.
7. For each news item it just stored, the worker asks Anthropic Haiku for the dated school events in it, resolving relative references such as "this Thursday" against the message's own send date. Events are stored as `TrackedEvents`, deduplicated per school, date, and title, and a later message can move one (`Superseded`) or call it off (`Cancelled`). A failed extraction costs only that item's dates, never the news item or the digest.
8. The summary generator selects every `NewsItem` that has not been reported in a delivered digest yet, oldest first, up to 60 per digest. Eligibility is the recorded fact `NewsItem.IncludedInSummaryId`, not a date window, so nothing is skipped for being old and nothing is reported twice. The last 12 delivered digests are folded into a coverage index, and the "Important Upcoming Dates" section is rendered in C# from the active `TrackedEvents` dated today or later, then handed to the model to copy rather than asked for as output.
9. Anthropic Sonnet produces the weekly Markdown digest. A response that stopped at the token ceiling is never sent: the batch is halved and generation is retried, so a backlog large enough to truncate can still drain. A refusal is never sent either.
10. The draft is reviewed twice: a deterministic validator checks weekday and date agreement, future-only upcoming dates, chronological ordering, and that every rendered upcoming date survived into the output; an AI critic checks the draft against the source items it was written from. Findings drive one bounded revision pass. The review can never block a send, and a revision replaces the draft only when it is still recognizably a digest and scores no worse than the draft.
11. The finished digest is stored before it is handed to SMTP, so a mail server that is down does not throw away a full model call.
12. The worker converts Markdown to HTML and sends the email. Only after delivery are the reported news items stamped with the summary that reported them, so an undelivered digest leaves its news owed to the parent rather than buried. Scheduled run state is recorded to avoid duplicate scheduled executions for the same day.

## Data model

| Table | Purpose |
| --- | --- |
| `Parents` | Parent records, TalkingPoints credentials, recipient emails, and active status |
| `Children` | Child records with school, starting grade, starting year, and emoji |
| `Messages` | Raw TalkingPoints messages, keyed uniquely per parent by external message ID |
| `NewsItems` | Persisted message-derived or newsletter-derived content with source type, and the digest that reported it (`IncludedInSummaryId`, null until reported) |
| `TrackedEvents` | Dated school events extracted from news items, unique per parent, school, date, and title, with status `Active`, `Superseded`, or `Cancelled` and a link to the event that superseded them |
| `Summaries` | Archived Markdown summaries, with the delivery timestamp, the review log, and how many revision passes ran |
| `PipelineRuns` | Scheduled run tracking with trigger, status, timestamps, and error text |

## Local development

- Use `docs/CLI.md` for the CLI command reference.
- Use `docs/F5-DEBUGGING.md` for Visual Studio and AppHost debugging.
- Run `dotnet build TalkingPointsSummary.sln` from the repo root to build the solution.
