# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Talking Points Summary turns school messages from TalkingPoints into weekly email digests for parents. The repo is a .NET 10 solution: a worker service/CLI, a Blazor Server admin UI, a shared Core library, and a .NET Aspire AppHost for local orchestration.

## Commands

Build the solution:

```bash
dotnet build TalkingPointsSummary.sln
```

Run all tests (unit + integration):

```bash
dotnet test TalkingPointsSummary.sln
```

Run a single test project:

```bash
dotnet test tests/TalkingPointsSummary.Tests
dotnet test tests/TalkingPointsSummary.IntegrationTests
```

Run a single test by name (xUnit filter):

```bash
dotnet test tests/TalkingPointsSummary.Tests --filter "FullyQualifiedName~MessageCategorizerTests"
```

Integration tests use Testcontainers and require a running Docker engine.

Run the worker locally (worker mode with no args starts the scheduler; any argument runs a CLI command and exits):

```bash
dotnet run --project src/TalkingPointsSummary -- <command> [options]
```

See `docs/CLI.md` for the full CLI command reference (`add-parent`, `add-child`, `list-parents`, `remove-parent`, `remove-child`, `run`, `check-config`).

Local stack options:

```bash
# Docker Compose (worker, admin, Browserless, Mailpit, PostgreSQL)
docker compose -f infra/docker-compose.yml --env-file .env up -d --build

# Aspire AppHost: set TalkingPointsSummary.AppHost as startup project and press F5
```

See `docs/F5-DEBUGGING.md` for Aspire/Visual Studio debugging details.

## Architecture

**Project layout:**
- `src/TalkingPointsSummary` — worker service + CLI entry point (`Commands/`, `Pipeline/`, `Prompts/`, `Configuration/`, `Data/`, `Migrations/`, `Services/`)
- `src/TalkingPointsSummary.Core` — shared models, EF Core `AppDbContext`, and parent/child domain services, referenced by both the worker and the admin UI
- `src/TalkingPointsSummary.Admin` — Blazor Server admin UI (`Components/Pages`), uses the same Core parent/child services as the CLI
- `src/TalkingPointsSummary.AppHost` — .NET Aspire orchestrator for local dev (manages PostgreSQL, Browserless, Mailpit containers)
- `tests/TalkingPointsSummary.Tests` — unit tests (xUnit, Moq, FluentAssertions, EF Core InMemory)
- `tests/TalkingPointsSummary.IntegrationTests` — integration tests using Testcontainers

**Pipeline flow** (see `src/TalkingPointsSummary/Pipeline/`), run weekly per the configured schedule or on demand via `run`/the debug endpoint:

1. Scheduler fires at the configured `PipelineSchedule` day/hour (timezone-aware); if blocked by an in-progress run or an error, it retries after one minute within the same scheduled hour, otherwise advances to the next week.
2. For each active parent, fetch TalkingPoints feed pages (20 messages/page), stopping at the newest stored message ID/timestamp, a short/empty page, or `TalkingPointsApi:MaxPagesPerRun`.
3. Deduplicate on `(ParentId, ExternalMessageId)` and persist only new `Messages`.
4. Categorize each unprocessed message with Anthropic Haiku: detect newsletter URLs, decide newsworthiness, generate short summary text.
5. If a newsletter URL is present, validate it, scrape via Browserless, store as a `NewsItem` (falls back to the original message text if scraping fails/empty).
6. Load up to six weeks of `NewsItems` and prior `Summaries` for the parent, generate the weekly Markdown digest with Anthropic Sonnet.
7. Convert Markdown to HTML, send via SMTP, archive the summary, record `PipelineRuns` state to avoid duplicate scheduled executions.

**Data model** (EF Core, migrations in `src/TalkingPointsSummary/Migrations/`): `Parents` -> `Children`, `Messages` (unique per parent by `ExternalMessageId`), `NewsItems`, `Summaries`, `PipelineRuns`.

**Configuration** loads in order: `appsettings.json` -> `appsettings.{Environment}.json` -> `appsettings.Local.json` -> user secrets (Development) -> environment variables. See the README configuration tables for the full settings list (worker, admin, AppHost). Notable: `DebugFeatures:Enabled` exposes `POST /debug/pipeline/run-now` on the worker and the admin debug page; `NewsletterScrapingSecurity` gates which hosts/schemes Browserless is allowed to scrape.

**CI/CD** (see `docs/CI-CD.md` for full detail): PRs run restore/build/unit/integration tests (`.github/workflows/pr.yml`); PRs must carry exactly one `semver:` label enforced by `label-gate.yml`, auto-applied by `classify-pr.yml` (Claude-based classification, human-overridable); `main.yml` builds/publishes both container images to `ghcr.io` on push to `main` and auto-tags releases from merged PR semver labels via `compute-version`. A `.github/skills/semver-classification/SKILL.md` skill defines the same major/minor/patch rules used by CI for classifying changes locally.

## Working conventions

- `TreatWarningsAsErrors` is enabled solution-wide (`Directory.Build.props`) — a build with warnings will fail.
- Do not select or introduce a new NuGet/npm package, ORM, logging framework, serializer, or other dependency not already referenced in the workspace without asking the user to choose first; verify presence via the `.csproj` files, not assumption.
- PR titles matter: they appear verbatim in GitHub release notes. Lead with user-facing impact, not implementation detail, and keep them under ~72 characters (see `.github/skills/create-pull-request/SKILL.md` and `.github/skills/semver-classification/SKILL.md` for the full rules and examples).
- Treat TalkingPoints `x-token`/`x-contactid` values and Anthropic/SMTP credentials as secrets; never commit them.
