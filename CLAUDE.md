# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Talking Points Summary fetches school messages from the TalkingPoints parent feed, uses Anthropic models to
classify and summarize them, scrapes newsletter links through Browserless when needed, and emails a weekly
Markdown digest through SMTP. See `README.md` for the full local setup (Docker Compose, Aspire AppHost, or
direct project runs) and the configuration reference table.

## Commands

```bash
# Restore and build (matches CI)
dotnet restore TalkingPointsSummary.sln
dotnet build TalkingPointsSummary.sln --configuration Release --no-restore

# Unit tests (fast, no external dependencies)
dotnet test tests/TalkingPointsSummary.Tests/TalkingPointsSummary.Tests.csproj --filter "Category!=Integration"

# Integration tests (require Docker for Testcontainers; some hit live services)
dotnet test tests/TalkingPointsSummary.IntegrationTests/TalkingPointsSummary.IntegrationTests.csproj --filter "Category=Integration"

# Single test
dotnet test tests/TalkingPointsSummary.Tests/TalkingPointsSummary.Tests.csproj --filter "FullyQualifiedName~PipelineOrchestratorTests.RunAsync_ShouldFetchMessages"

# CLI (from the worker project, or via the built DLL)
dotnet run --project src/TalkingPointsSummary -- check-config
dotnet run --project src/TalkingPointsSummary -- run
```

Both test projects are xUnit. Integration tests are marked with `[Trait("Category", "Integration")]` (see
`tests/TalkingPointsSummary.IntegrationTests/PipelineEndToEndTests.cs`); CI runs the two suites as separate
steps with that filter. `docs/CLI.md` has the full CLI command reference and `docs/F5-DEBUGGING.md` covers
Aspire AppHost debugging launch profiles.

**Build settings that affect all code changes** (`Directory.Build.props`): `TreatWarningsAsErrors` is `true`,
and `GenerateDocumentationFile` is `true` — every public member needs an XML doc comment or the build fails.
Test projects suppress the missing-doc-comment warnings (`Directory.Build.targets`).

## Architecture

### Projects

- `src/TalkingPointsSummary` — worker service and CLI (`System.CommandLine`), entry point in `Program.cs`
- `src/TalkingPointsSummary.Admin` — Blazor Server admin UI for managing parents/children and triggering debug runs
- `src/TalkingPointsSummary.Core` — shared EF Core `AppDbContext`, models, options classes, and `ParentService`/`ChildService` (used by both the CLI and the Admin UI)
- `src/TalkingPointsSummary.AppHost` — .NET Aspire orchestrator for local F5 debugging (manages Postgres, Browserless, Mailpit containers)

### Worker process modes (`Program.cs`)

`Main` branches into one of three modes depending on args and config:

1. **CLI mode** (`args.Length > 0`) — builds a `ServiceCollection`, runs `CommandHandler.BuildRootCommand`, executes one command, exits.
2. **Debug worker mode** (`DebugFeatures:Enabled=true`, no args) — a full `WebApplication` host that also runs the background pipeline service and exposes `POST /debug/pipeline/run-now` (used by the Admin debug page and the AppHost `(run now)`/`(check-config)` launch profiles).
3. **Plain worker mode** (default, no args) — a bare `Host` running `WeeklyPipelineService` as a hosted service with no HTTP surface.

All three modes apply EF Core migrations and run `StartupValidator` before doing anything else; any failed
check calls `Environment.Exit(1)`.

### Pipeline (`Pipeline/PipelineOrchestrator.cs`)

Per-parent flow, one step feeding the next:

`Fetch (ITalkingPointsApiClient) -> Dedup (IMessageDeduplicator) -> Categorize each unprocessed message (IMessageCategorizer, Anthropic Haiku) -> route by category:`
- newsletter URL present -> scrape via `INewsletterScraper` (Browserless), falling back to raw message text if the scrape is empty
- message is newsworthy on its own -> store the message text directly

`-> persist NewsItems in a transaction -> ISummaryGenerator builds a prompt from up to six weeks of NewsItems/prior Summaries -> Anthropic Sonnet generates Markdown -> IMarkdownConverter to HTML -> IEmailSender (SMTP) -> archive the Summary row`.

The `Summary` row is persisted with `Content = null` *before* the AI call, so a failed/empty AI response still
leaves the prompt on record for debugging. `WeeklyPipelineService` (also in `Pipeline/`) owns the weekly
schedule and guards against overlapping runs (`PipelineStartStatus`); `PipelineOrchestrator` only ever runs
one parent at a time.

### AI provider abstraction

`TalkingPointsSummary.Core.Services.IAiClient` is the provider-agnostic interface (`CompleteAsync`,
`ValidateCredentialsAsync`); `Services/Anthropic/AnthropicAiClient.cs` is the only implementation today.
`Ai:Provider` in config selects the provider; prompts live as text templates in `Prompts/` and are assembled
by `MessageCategorizationPromptBuilder` / `SummaryPromptBuilder`.

### Configuration migration system (`Configuration/`)

Startup config passes through `ConfigMigrationRunner`, which applies the rules in `ConfigKeyMigrations.All`
against the live `IConfiguration` before anything else reads it. Each `ConfigKeyMigration` promotes an old key
to a new key (only when the old key is set and the new key isn't already), can inject companion defaults (e.g.
promoting the legacy flat `Anthropic:ApiKey` also sets `Ai:Provider=Anthropic`), and emits a deprecation
warning that gets logged at startup. When renaming or restructuring a config key, add a migration here instead
of breaking existing deployments.

### Data model

`Parents` -> `Children` (1:many); `Messages` (raw TalkingPoints feed, unique per `(ParentId, ExternalMessageId)`)
-> `NewsItems` (message- or newsletter-derived content, keyed by `SourceType`) -> `Summaries` (archived weekly
Markdown). `PipelineRuns` tracks scheduled run state to prevent duplicate runs in the same scheduled hour.

## CI/release conventions

Every PR needs exactly one `semver: major|minor|patch` or `skip-release` label (enforced by a required status
check, `label-gate.yml`); `classify-pr.yml` proposes a label automatically via Claude but a human can override
it. Merging to `main` auto-tags and publishes `ghcr.io` images for the worker and admin — see `docs/CI-CD.md`
for the full pipeline and `docs/version-strategy.md` for the versioning rules. Direct pushes to `main` are not
used; all changes go through PRs.
