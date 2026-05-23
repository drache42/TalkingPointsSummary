# CI/CD

This repository uses GitHub Actions for pull request validation, main-branch publishing, and Dependabot automation.

## Workflows

### PR validation

The pull request workflow is defined in `.github/workflows/pr.yml`.

- Trigger: every pull request opened, synchronized, or reopened against `main`
- Runs: restore, build, unit tests, and integration tests on `ubuntu-latest`
- Docker support: integration tests rely on Testcontainers and use the Docker engine already available on GitHub-hosted Ubuntu runners
- Test reporting: `dotnet test` writes TRX output and the workflow publishes a GitHub job summary with suite totals and uploads the raw TRX files as an artifact
- Merge policy: branch protection should require this workflow to pass before merging

Discord notifications are sent for failed PR runs only when the pull request branch lives in this repository. Fork-based PRs do not receive Discord notifications because repository secrets are not exposed to forked pull request workflows.

### Label gate

The label gate is defined in `.github/workflows/label-gate.yml` and is configured as a required status check on `main`.

- Trigger: every pull request opened, labeled, unlabeled, or reopened against `main`
- Runs: checks that the PR carries exactly one qualifying label — `semver: major`, `semver: minor`, `semver: patch`, or `skip-release`
- Pass: merge button is enabled
- Fail: merge button is disabled until a qualifying label is applied and the check reruns

`semver: unknown` does not pass the gate. A human must replace it with a concrete label before the PR can merge.

### PR classification

The classification workflow is defined in `.github/workflows/classify-pr.yml`.

- Trigger: every pull request opened or synchronized against `main` (uses `pull_request_target` so it has access to secrets)
- Behavior: fetches the PR diff via the GitHub API, calls Claude 3.5 Haiku to classify the change as `major`, `minor`, or `patch`, removes any stale `semver:` label, applies the new label, and posts or updates a bot comment with the classification and rationale
- Fork PRs: classification is skipped; a comment is posted instructing the maintainer to label manually
- API failure: `semver: unknown` is applied and the label gate blocks merge until a human resolves it

Classification runs automatically but humans can override the label at any time. The label gate checks the label, not the classification source.

### Main pipeline

The main pipeline is defined in `.github/workflows/main.yml`.

- Trigger: every push to `main`
- Manual trigger: `workflow_dispatch` is enabled so you can run the validation path against a selected branch from the Actions tab
- Runs: restore, build, unit tests, and integration tests
- Concurrency: runs are serialized with `cancel-in-progress: false` so queued merges complete in order

On success for `main` pushes, three jobs run in sequence after `validate`:

1. **`compute-version`** — queries all PRs merged since the last release tag, takes the highest `semver:` label, and computes the next version. If all merged PRs carry `skip-release`, outputs `skip=true` and no release is created.
2. **`publish-main-images`** — builds and pushes both container images to `ghcr.io`, stamping `VERSION` and `COMMIT_SHA` into the binaries via `dotnet publish`.
3. **`auto-release`** — skipped when `skip=true`. Creates an annotated git tag and promotes both images to versioned release tags.

Published tags after every `main` push:

- Worker: `ghcr.io/<owner>/<repo>-worker:main`
- Worker: `ghcr.io/<owner>/<repo>-worker:sha-<shortsha>`
- Admin: `ghcr.io/<owner>/<repo>-admin:main`
- Admin: `ghcr.io/<owner>/<repo>-admin:sha-<shortsha>`

Additional tags published when `auto-release` runs:

- `<major>.<minor>.<patch>`
- `<major>.<minor>`
- `latest`

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
- Release identity is computed automatically from PR semver labels

### Automated release flow

Releases are created automatically when a PR merges to `main`. The `compute-version` job reads the `semver:` labels of all PRs merged since the last release tag and bumps the version accordingly. No manual tagging is required for day-to-day releases.

PRs labelled `skip-release` merge without producing a release. When all PRs since the last tag carry `skip-release`, the release step is skipped entirely and only the `main` and `sha-*` images are published.

### Semver rules

- `semver: major` — incompatible changes to operator-visible behavior such as configuration keys, CLI contracts, image behavior, or deployment expectations
- `semver: minor` — backward-compatible new features
- `semver: patch` — backward-compatible fixes and security updates

## Required repository settings and secrets

### GitHub settings

Enable these repository settings:

1. Configure a branch ruleset targeting `main` with `label-gate` as a required status check
2. Enable auto-merge in the repository settings so Dependabot PRs can enter GitHub's built-in auto-merge flow

### Secrets

| Secret | Required | Purpose |
| --- | --- | --- |
| `ANTHROPIC_API_KEY` | Yes | Enables `classify-pr.yml` to call the Anthropic API for automatic PR classification. Without this, classification fails and `semver: unknown` is applied. |
| `DISCORD_WEBHOOK_URL` | No | Discord webhook URL for pipeline failure, release, and Dependabot breaking-change notifications. |

### Repository variables

| Variable | Required | Purpose |
| --- | --- | --- |
| `ANTHROPIC_MODEL` | Yes | The Anthropic model ID used by `classify-pr.yml` (e.g. `claude-haiku-4-5`). Update this variable to switch models without modifying workflow files. |

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

This is useful for testing the validation portion of `main.yml` on a feature branch. Manual runs do not publish images because the publish jobs are gated to real `push` events for `main`.

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
