# Public Release Audit Report

Date: 2026-03-10

## Executive Summary

This repository is not ready to go public. The main blockers are tracked privacy and anonymization artifacts in docs and test fixtures, a credential-like example token and contact ID in the CLI docs, stale public setup documentation, and several internal-only guidance files that do not belong in a public repository without an explicit decision to publish them.

## Findings

### High

#### Credential-like values and named examples remain in public docs

What was found:
The tracked CLI documentation contains a credential-like TalkingPoints `x-token` string and contact ID alongside named family and school examples.

Why it matters:
This is a public-release blocker until the maintainer confirms those values are synthetic and intentionally publishable or replaces them with clearly fake placeholders.

File references:
- [docs/CLI.md](docs/CLI.md#L42-L43)
- [docs/CLI.md](docs/CLI.md#L116)
- [README.md](README.md#L119-L137)

#### Real-looking names and school names remain in tracked tests and fixtures

What was found:
Tracked tests and fixtures still contain real-looking personal names and school names.

Why it matters:
Anonymization is incomplete beyond the public docs, and public repos are fully searchable.

File references:
- [tests/TalkingPointsSummary.Tests/GradeCalculatorTests.cs](tests/TalkingPointsSummary.Tests/GradeCalculatorTests.cs#L45)
- [tests/TalkingPointsSummary.Tests/GradeCalculatorTests.cs](tests/TalkingPointsSummary.Tests/GradeCalculatorTests.cs#L60)
- [tests/TalkingPointsSummary.Tests/Fixtures/sample-api-response.json](tests/TalkingPointsSummary.Tests/Fixtures/sample-api-response.json#L15)
- [tests/TalkingPointsSummary.Tests/Fixtures/sample-api-response.json](tests/TalkingPointsSummary.Tests/Fixtures/sample-api-response.json#L31)
- [tests/TalkingPointsSummary.Tests/Fixtures/sample-browserless-response.json](tests/TalkingPointsSummary.Tests/Fixtures/sample-browserless-response.json#L6)

### Medium

#### `.env.example` is stale and does not match the active configuration model

What was found:
The tracked environment template advertises `CONNECTION_STRING`, `BROWSERLESS_URL`, `SCHEDULE_DAY`, and `SCHEDULE_HOUR`, while the app reads section-based configuration and resolves `ConnectionStrings:TalkingPoints` and `PipelineSchedule` through hierarchical binding.

Why it matters:
A new contributor who follows the template will not configure the app correctly.

File references:
- [.env.example](.env.example#L2-L11)
- [src/TalkingPointsSummary/Configuration/WorkerConfiguration.cs](src/TalkingPointsSummary/Configuration/WorkerConfiguration.cs#L39)
- [src/TalkingPointsSummary/Configuration/WorkerConfiguration.cs](src/TalkingPointsSummary/Configuration/WorkerConfiguration.cs#L97-L104)
- [src/TalkingPointsSummary/Configuration/WorkerConfiguration.cs](src/TalkingPointsSummary/Configuration/WorkerConfiguration.cs#L159)
- [README.md](README.md#L40-L53)

#### CLI documentation is stale because it omits `check-config`

What was found:
The shipped CLI exposes a `check-config` command and the CLI reference does not document it.

Why it matters:
One of the most useful setup-validation commands is missing from the public reference.

File references:
- [docs/CLI.md](docs/CLI.md#L20-L212)
- [src/TalkingPointsSummary/Commands/CommandHandler.cs](src/TalkingPointsSummary/Commands/CommandHandler.cs#L246)
- [src/TalkingPointsSummary/Properties/launchSettings.json](src/TalkingPointsSummary/Properties/launchSettings.json#L19-L21)
- [src/TalkingPointsSummary.AppHost/Properties/launchSettings.json](src/TalkingPointsSummary.AppHost/Properties/launchSettings.json#L29-L39)

#### README incompletely explains the public repository shape

What was found:
The README presents the project as a single worker service, while the repo also ships an admin web app and an Aspire AppHost. The Docker quick start does not tell a public evaluator that the admin UI is exposed on port 5100.

Why it matters:
This weakens first-run comprehension for outside contributors evaluating the repository.

File references:
- [README.md](README.md#L3)
- [README.md](README.md#L85-L149)
- [docker-compose.yml](docker-compose.yml#L31-L45)
- [src/TalkingPointsSummary.AppHost/Program.cs](src/TalkingPointsSummary.AppHost/Program.cs#L10-L84)
- [src/TalkingPointsSummary.Admin/Program.cs](src/TalkingPointsSummary.Admin/Program.cs#L14-L31)

### Low

#### Internal-only workflow material is tracked in the repository

What was found:
The repository includes internal working documents and agent-operating instructions.

Why it matters:
These files are not secrets, but they are not public-facing product or contributor documentation and add noise to a public repository.

File references:
- [docs/TODO.md](docs/TODO.md#L1-L14)
- [docs/unit-test-prompt.md](docs/unit-test-prompt.md#L1-L51)
- [.github/copilot-instructions.md](.github/copilot-instructions.md#L2-L46)
- [.github/skills/postgres-database-access/SKILL.md](.github/skills/postgres-database-access/SKILL.md#L38-L69)

## Docker and Repo Structure Assessment

The Docker and Docker Compose files are in the right place. Keeping [Dockerfile](Dockerfile), [Dockerfile.admin](Dockerfile.admin), and [docker-compose.yml](docker-compose.yml) at the repo root is conventional for a multi-service repository, and the local-development orchestration project living under [src/TalkingPointsSummary.AppHost/Program.cs](src/TalkingPointsSummary.AppHost/Program.cs#L10-L84) also makes sense.

The issue is documentation clarity, not file placement. [docker-compose.yml](docker-compose.yml#L31-L45) defines an admin service and publishes port 5100, while [README.md](README.md#L85-L149) walks through Docker startup and verification without explaining that the admin UI exists, what it is for, or when a contributor should use Docker Compose versus the Aspire AppHost.

## Documentation Assessment

The docs are not complete, current, and release-focused enough for a public repo.

Classification:
- Public-facing documentation: [README.md](README.md), [docs/CLI.md](docs/CLI.md), [.env.example](.env.example)
- Contributor or maintainer documentation: [docs/F5-DEBUGGING.md](docs/F5-DEBUGGING.md)
- Internal-only documentation that does not belong in a public repo by default: [docs/TODO.md](docs/TODO.md), [docs/unit-test-prompt.md](docs/unit-test-prompt.md), [.github/copilot-instructions.md](.github/copilot-instructions.md), [.github/skills/postgres-database-access/SKILL.md](.github/skills/postgres-database-access/SKILL.md)

Concrete documentation gaps:
- [docs/CLI.md](docs/CLI.md#L20-L212) omits `check-config`.
- [README.md](README.md#L3) describes only the worker, while the tracked repo contains a worker, admin UI, and AppHost.
- [README.md](README.md#L85-L149) does not explain how to access the admin UI started by Docker Compose.
- [.env.example](.env.example#L2-L11) is out of sync with the active configuration model documented in [README.md](README.md#L40-L53) and implemented in [src/TalkingPointsSummary/Configuration/WorkerConfiguration.cs](src/TalkingPointsSummary/Configuration/WorkerConfiguration.cs#L97-L104).

## Privacy and Anonymization Risks

The following tracked values need review before public release:
- The previously used family-name examples in public docs and tests. References: [README.md](README.md#L119), [docs/CLI.md](docs/CLI.md#L32-L50), [tests/TalkingPointsSummary.Tests/GradeCalculatorTests.cs](tests/TalkingPointsSummary.Tests/GradeCalculatorTests.cs#L45), [tests/TalkingPointsSummary.Tests/GradeCalculatorTests.cs](tests/TalkingPointsSummary.Tests/GradeCalculatorTests.cs#L60)
- The previously used student-name examples in public docs, tests, and fixtures. References: [README.md](README.md#L128-L137), [docs/CLI.md](docs/CLI.md#L77-L118), [tests/TalkingPointsSummary.Tests/Fixtures/sample-api-response.json](tests/TalkingPointsSummary.Tests/Fixtures/sample-api-response.json#L15-L47)
- The previously used school-name examples in public docs, tests, and fixtures. References: [README.md](README.md#L129), [README.md](README.md#L137), [docs/CLI.md](docs/CLI.md#L78), [docs/CLI.md](docs/CLI.md#L117-L118), [tests/TalkingPointsSummary.Tests/Fixtures/sample-browserless-response.json](tests/TalkingPointsSummary.Tests/Fixtures/sample-browserless-response.json#L6)
- A credential-like `x-token` string and contact ID in the CLI docs. References: [docs/CLI.md](docs/CLI.md#L42), [docs/CLI.md](docs/CLI.md#L43), [docs/CLI.md](docs/CLI.md#L116)
- Public email examples are present, but the tracked ones I found are generic placeholders rather than unique identifiers. References: [README.md](README.md#L72), [docs/CLI.md](docs/CLI.md#L44), [.env.example](.env.example#L7-L9)

I did not find the extra audit-only sample names in the tracked-file inventory. The open [docs/public-release-audit-prompt.md](docs/public-release-audit-prompt.md) attachment contains additional sample names, but that file was not present in the tracked-file list used for the audit findings.

## Source-Control Hygiene Risks

The repository’s ignore coverage is generally good. [.gitignore](.gitignore#L7) ignores `appsettings.Local.json`, and [.gitignore](.gitignore#L15), [.gitignore](.gitignore#L48-L60) cover `.env` files, `obj`, `logs`, `bin` output, and `.vs`. The tracked-file inventory I inspected did not include bin, obj, logs, or `appsettings.Local.json`, so I did not find tracked build artifacts or tracked local override files.

The tracked hygiene risks are instead these public-scope content choices:
- Internal-only workflow docs are tracked and would ship publicly unless removed or moved. References: [docs/TODO.md](docs/TODO.md), [docs/unit-test-prompt.md](docs/unit-test-prompt.md), [.github/copilot-instructions.md](.github/copilot-instructions.md), [.github/skills/postgres-database-access/SKILL.md](.github/skills/postgres-database-access/SKILL.md)
- The public environment template is stale and would mislead setup. References: [.env.example](.env.example#L2-L11), [src/TalkingPointsSummary/Configuration/WorkerConfiguration.cs](src/TalkingPointsSummary/Configuration/WorkerConfiguration.cs#L97-L104)

## Approval-Gated Next Steps

### Safe doc cleanup

1. Replace all named family, student, school, token, and contact ID examples in [README.md](README.md), [docs/CLI.md](docs/CLI.md), and tracked test fixtures with clearly synthetic placeholders.
2. Update [.env.example](.env.example) to the current hierarchical variable names.
3. Add `check-config` to [docs/CLI.md](docs/CLI.md).
4. Update [README.md](README.md) to describe the admin UI and the Docker Compose versus AppHost workflow.
5. Remove or relocate internal-only files from the public repo scope unless you explicitly want to publish the internal AI workflow material.

### Requires maintainer judgment

1. Confirm whether the previously used family, student, and school example names, the token in [docs/CLI.md](docs/CLI.md#L42), and the contact ID in [docs/CLI.md](docs/CLI.md#L43) are synthetic.
2. Decide whether the admin UI and debug-oriented capabilities are part of the public product story or maintainer-only tooling. That decision drives how prominently they belong in [README.md](README.md) and whether any debug-focused instructions need tighter scoping in public documentation.