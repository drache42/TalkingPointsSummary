# CI/CD

This repository uses GitHub Actions for pull request validation, main-branch publishing, release tag promotion, and Dependabot automation.

## Workflows

### PR validation

The pull request workflow is defined in `.github/workflows/pr.yml`.

- Trigger: every pull request opened, synchronized, or reopened against `main`
- Runs: restore, build, unit tests, and integration tests on `ubuntu-latest`
- Docker support: integration tests rely on Testcontainers and use the Docker engine already available on GitHub-hosted Ubuntu runners
- Test reporting: `dotnet test` writes TRX output and the workflow publishes a GitHub job summary with suite totals and uploads the raw TRX files as an artifact
- Merge policy: branch protection should require this workflow to pass before merging

Discord notifications are sent for failed PR runs only when the pull request branch lives in this repository. Fork-based PRs do not receive Discord notifications because repository secrets are not exposed to forked pull request workflows.

### Main pipeline

The main pipeline is defined in `.github/workflows/main.yml`.

- Trigger: every push to `main`
- Manual trigger: `workflow_dispatch` is enabled so you can run the validation path against a selected branch from the Actions tab
- Runs: restore, build, unit tests, and integration tests
- On success for `main` pushes: builds and publishes both container images to `ghcr.io`

Published branch tags:

- Worker: `ghcr.io/<owner>/<repo>-worker:main`
- Worker: `ghcr.io/<owner>/<repo>-worker:sha-<shortsha>`
- Admin: `ghcr.io/<owner>/<repo>-admin:main`
- Admin: `ghcr.io/<owner>/<repo>-admin:sha-<shortsha>`

`latest` is intentionally not updated on ordinary pushes to `main`.

### Release tag promotion

The same main pipeline also handles stable releases.

- Trigger: pushing a git tag that matches `v<major>.<minor>.<patch>`
- Guardrail: the tagged commit must already be contained in `origin/main`
- Behavior: the workflow promotes the already-published `sha-<shortsha>` images to stable release tags instead of rebuilding them

Published stable tags:

- `<major>.<minor>.<patch>`
- `<major>.<minor>`
- `latest`

This keeps semver human-controlled while preserving an exact link between the stable release tag and the image built from the main-branch commit.

### Dependabot

Dependabot is configured in `.github/dependabot.yml`.

- Scope: NuGet, Dockerfiles in `infra/`, and GitHub Actions versions
- Schedule: weekly
- Target branch: `main`

Dependabot auto-merge is defined in `.github/workflows/automerge.yml`.

- Trigger: Dependabot pull requests targeting `main`
- Eligible updates: semver patch and semver minor updates
- Merge mode: GitHub auto-merge using the normal PR merge flow
- Safety gate: branch protection and required checks still apply, so the pull request does not merge until all required status checks pass
- Major updates: never auto-merge

## Release versioning

Container versioning is split into build identity and release identity.

- Build identity comes from `main` branch publishes: `sha-<shortsha>` and `main`
- Release identity comes from manual semver tags such as `v1.2.3`

Recommended release flow:

1. Merge changes into `main`
2. Wait for the `main.yml` workflow to publish `sha-<shortsha>` images
3. Decide the semantic version manually based on compatibility impact
4. Create and push a git tag such as `v1.2.3` on that exact commit
5. Let `main.yml` promote the `sha-<shortsha>` images to `1.2.3`, `1.2`, and `latest`

Use semver conservatively:

- `MAJOR`: incompatible changes to operator-visible behavior such as configuration keys, CLI contracts, image behavior, or deployment expectations
- `MINOR`: backward-compatible new features
- `PATCH`: backward-compatible fixes and security updates

## Required repository settings and secrets

### GitHub settings

Enable these repository settings:

1. Protect `main` and require the PR validation workflow to pass before merge
2. Enable auto-merge in the repository settings so Dependabot PRs can enter GitHub's built-in auto-merge flow

### Secrets

Add this repository secret if you want Discord notifications:

- `DISCORD_WEBHOOK_URL`: Discord webhook URL for failure notifications from trusted workflows

No separate registry secret is required for `ghcr.io` publishing. The workflows use `GITHUB_TOKEN` with `packages: write` permission.

## How to test CI/CD changes before merging

### Simplest path: draft PR from a feature branch

The lowest-friction way to test workflow changes is to push them to a feature branch and open a draft PR against `main`.

- This exercises the real GitHub-hosted runner environment
- It validates the pull request workflow exactly as branch protection will see it
- It avoids touching `main`

This is the recommended primary workflow for testing CI changes.

### Manual validation with `workflow_dispatch`

`main.yml` includes `workflow_dispatch`, so once that workflow file exists on the default branch you can manually run it from the Actions tab and pick a branch.

This is useful for testing the validation portion of `main.yml` on a feature branch. Manual runs do not publish images because the publish jobs are gated to real `push` events for `main` and release tags.

### Local execution with `act`

You can also use `act` locally for fast iteration.

Example entry points:

```bash
act pull_request -W .github/workflows/pr.yml
act workflow_dispatch -W .github/workflows/main.yml
```

Use `act` for quick feedback on YAML structure, shell logic, and basic workflow wiring. Do not treat it as a perfect replica of GitHub-hosted runners.

Known limitations:

- Runner images differ from GitHub-hosted runners
- Docker Buildx and registry behavior can differ locally
- Dependabot and fork-permission behavior cannot be reproduced exactly

Use `act` for iteration and a draft PR for final verification.

## How auto-merge is controlled

Dependabot auto-merge is implemented by enabling GitHub's built-in auto-merge on eligible PRs. It does not bypass branch protection.

To override it:

1. Open the Dependabot PR in GitHub
2. Disable auto-merge in the pull request UI if you want to hold the update for manual review
3. Merge manually or leave it open

If you require additional reviewer approvals on `main`, those approval requirements still apply unless you separately adjust your branch protection policy.
