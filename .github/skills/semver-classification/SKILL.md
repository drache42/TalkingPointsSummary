---
name: semver-classification
description: Analyzes a set of code changes (diff, commit log, or described changes) and recommends the correct semantic version bump (patch / minor / major) for the TalkingPointsSummary project. Use this when asked to classify changes, suggest a version bump, or determine what semver level applies to a diff.
---

# Skill: Semver Classification

**Project**: `drache42 / TalkingPointsSummary`

This skill classifies a set of changes according to [Semantic Versioning 2.0.0](https://semver.org) and reports a recommended bump with a rationale.

---

## What Counts as the "Public API" for This Project

Because this is an application (not a library), the "public API" is defined as all contracts that an existing deployment depends on:

| Area | Examples |
|---|---|
| **Configuration schema** | Keys in `appsettings.json`, `appsettings.*.json`, and `Configuration/` option classes |
| **Database schema** | EF Core migration files in `Migrations/` |
| **CLI commands** | Command classes in `Commands/` |
| **Email / report output** | Template structure or field names in `Prompts/` that change what is sent to end users |
| **External integrations** | Changes to outbound API contracts, webhook payloads, or scraper contracts |

---

## Step 1 — Gather the Changes (if not already provided)

If the user has not already provided a diff or description, run:

```powershell
# Commit log vs. base branch (defaults to origin/main)
git -C "d:\Code\TalkingPointsSummary" log origin/main..HEAD --oneline

# File-level summary
git -C "d:\Code\TalkingPointsSummary" diff origin/main...HEAD --stat

# Full diff
git -C "d:\Code\TalkingPointsSummary" diff origin/main...HEAD
```

If the user specifies a different base (e.g. `origin/develop`), replace `origin/main` in the commands above.

> If the diff is very large (> 400 lines), read `--stat` and the commit log first, then target specific files with `git diff origin/main...HEAD -- <path>`.

---

## Step 2 — Classify the Bump

Apply the **highest** matching tier. Check tiers top-down and stop at the first match.

---

### `major` — Breaking change to an existing deployment

Classify as `major` if the diff contains **any** of:

**Database schema — destructive**
- A migration file containing `DROP TABLE`, `DROP COLUMN`, `TRUNCATE`, or `ALTER COLUMN` that changes a column's type or nullability in a way that loses data or breaks existing queries

**Configuration — removed or renamed**
- A key deleted or renamed in `appsettings.json`, `appsettings.*.json`, or any `Configuration/` option class property — an existing deployment that still uses the old key will fail validation or produce a silent misconfiguration

**CLI — removed or renamed**
- A command class in `Commands/` is deleted or its verb/name is changed — scripts or documentation that reference the old command break

**Output contract — structurally broken**
- The email/newsletter template changes in a way that removes or renames fields that end-users or downstream consumers depend on

**Explicit marker**
- Any commit message or code comment containing `BREAKING CHANGE:` or `// BREAKING CHANGE`

---

### `minor` — New backward-compatible functionality

Classify as `minor` (if no `major` signals) when the diff contains **any** of:

- A new service, class, or pipeline stage is added (`Services/`, `Pipeline/`)
- A new EF Core migration that only **adds** tables or columns (no drops, no destructive alters)
- A new configuration option is introduced with a safe default (existing deployments still work without setting it)
- A new admin UI page or Blazor component (`TalkingPointsSummary.Admin/Components/`)
- A new CLI command (`Commands/`)
- A new AI prompt file or pipeline step (`Prompts/`, `Pipeline/`)
- A new external integration or data source is wired in
- A new public-facing feature that does not alter existing behavior

---

### `patch` — Backward-compatible fix or maintenance

Default when no `major` or `minor` signals are present. Typical `patch` signals:

- Bug fix (commit messages contain "fix", "bug", "patch", "correction", "resolve", "issue")
- Changes entirely inside `tests/` with no production code changes
- Changes entirely inside `docs/` or `*.md` files
- Refactor or cleanup with no behavioral change ("refactor", "cleanup", "chore", "tidy", "rename internal")
- Build, CI, or infrastructure changes (`infra/`, `.github/`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`)
- Logging or observability improvements with no logic change
- Dependency version bump (patch or minor version of an existing package)
- Test helper / fixture improvements with no production code change

---

## Step 3 — Suggest a PR Title

The PR title appears verbatim in **GitHub release notes**, so it must be meaningful to someone reading a changelog — not just the developer who wrote the code.

### Title Rules

- **Lead with the user-facing impact**, not the implementation detail
  - ✅ `Add per-feed scheduling for digest delivery`
  - ❌ `Refactor DigestSchedulerService constructor injection`
- **Be specific enough to distinguish this release from others** — avoid vague titles like `Bug fix` or `Updates`
- **Keep it under ~72 characters** so it renders cleanly in release notes and email digests
- **Use sentence case** (capitalize first word and proper nouns only)
- **Do not prefix with the semver level** (e.g. avoid `[minor]` or `feat:`) — GitHub release notes already group by tag; the prefix is noise
- **Do mention the affected area** when it helps readers understand scope (e.g. `Admin UI`, `CLI`, `digest email`, `feed scraper`)
- If the change is a **breaking change**, append ` — breaking change` at the end so it is unmissable in release notes

### Title Examples by Bump Level

| Bump | Example title |
|---|---|
| `patch` | `Fix duplicate entries in weekly digest when feed returns stale items` |
| `patch` | `Upgrade Azure SDK packages to address security advisory` |
| `minor` | `Add per-feed scheduling for digest delivery` |
| `minor` | `Add admin UI page for managing feed sources` |
| `major` | `Rename \`run\` CLI command to \`execute\` — breaking change` |
| `major` | `Remove legacy \`SmtpDelivery\` configuration key — breaking change` |

---

## Step 4 — Report the Result

Present the classification in this format:

```
**Suggested PR title:** <title following the rules in Step 3>

**Suggested semver bump:** `patch` | `minor` | `major`

**Rationale:** <One sentence naming the primary signal, e.g.:
  "A new `DigestSchedulerService` class is introduced with no breaking changes to config or schema."
  "Migration 20260314_AddCategoryTable.cs adds a new table with no drops or destructive alters."
  "The `run` CLI command is renamed to `execute`, breaking existing scripts."
>
```

If multiple tiers were triggered, list them briefly before stating the final verdict:
```
- `minor` signal: new `FeedFetcherService` class
- `patch` signals: updated unit tests, README fix
→ **Final: `minor`**
```
