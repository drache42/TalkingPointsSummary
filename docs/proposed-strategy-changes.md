# Proposed Changes to Version Strategy

This document lists proposed changes to the versioning strategy, to be reviewed one at a time.
Each change is self-contained and can be discussed and approved independently.

---

## ✅ Change 1 — Labels Are the Source of Truth; AI Is Optional *(applied)*

**What changes:**
The current strategy describes AI as the classification mechanism. This should be inverted: the *PR label* is the enforced artifact, and AI is just one possible way to set it.

**Current behavior:**
The pipeline calls an AI service, which produces a label. The label gate checks for the label.

**Proposed behavior:**
The label gate enforces the same rule (a qualifying `semver:` or `skip-release` label must exist before merge is permitted). How the label got there is irrelevant to the pipeline:
- A human reviewer applied it manually
- A bot or AI service suggested it and a human confirmed
- An AI service set it automatically with no human touch

The classification rules (what makes something `major`, `minor`, or `patch`) remain the same. The system just does not require AI to be the actor.

**Why:**
- Org policy may restrict or change AI availability
- Some teams prefer human-only classification
- The label gate works identically regardless

---

## ✅ Change 2 — Package-Level Versioning for Multi-Package Repositories *(applied)*

**What changes:**
The current strategy versions the entire repository as one unit (one tag, one version bump per release). This produces the unnecessary churn described in the RenovateBot headwinds document.

**Proposed behavior:**
If a repository publishes multiple packages, each package is versioned independently.

Core rules:
1. Each package has a defined **root path** (e.g., `src/Package.A/`, `src/Package.B/`)
2. The merge pipeline determines which packages were touched by examining which files changed since the last release of each package
3. Only packages with changed files get a new version; unaffected packages are not republished
4. The semver label on the PR applies to all affected packages — if a PR touches two packages at `minor` level, both get a minor bump

**Tag format for per-package versioning:**
Two options are available:

| Option | Tag format | Example |
|---|---|---|
| A | `<PackageName>/v<major>.<minor>.<patch>` | `Package.Core/v2.1.0` |
| B | `v<PackageName>-<major>.<minor>.<patch>` | `vPackage.Core-2.1.0` |

Option A is preferred: it is readable, glob-filterable per package (`git tag -l 'Package.Core/v*'`), and matches conventions used by Changesets and similar tooling.

**Pipeline impact:**
The merge pipeline replaces the single "find last repo tag" step with a per-package "find last package tag" lookup. The rest of the logic (label aggregation, version bump, tag creation, publish) runs once per affected package.

**Repositories with a single package:**
No change. Single-package repos behave identically to the current strategy.

---

## ✅ Change 3 — Merge Strategy: GitHub PR API for PR Tracking *(applied)*

**Context:** Code lives in GitHub. Pipelines run in Jenkins or ADO. This is relevant because the pipeline's execution environment is separate from the VCS — any pipeline can call the GitHub REST API over HTTP using a PAT stored as a pipeline secret. The pipeline host does not constrain which API gets called.

**What changes:**
The current strategy mandates squash-only merges because it walks git log to find which PRs merged since the last tag — squash gives exactly one commit per PR, making the mapping trivial. This restriction is worth relaxing. Two options are presented; only one needs to be chosen.

---

**Option A — Keep squash-only (no change from current)**

One commit per PR. The commit-to-PR mapping is trivially reliable via git log alone — no API call needed.

- Pros: Simple. Already designed. No external dependency at version aggregation time.
- Cons: Engineers lose individual commit history; all commits in a PR are collapsed into one.

---

**Option B — Switch from git log to GitHub PR API for PR tracking *(recommended)***

Instead of walking git history, the pipeline calls the GitHub REST API to find all PRs merged to `main` since the last tag was created:

```
GET https://api.github.com/repos/{owner}/{repo}/pulls?state=closed&base=main&sort=updated&direction=desc
```

Filtered to PRs whose `merged_at` timestamp is after the **tagged commit's** timestamp (obtained via `git log -1 --format=%cI <last-tag>`). Use the commit timestamp, not the tag object's creation time — annotated tags carry their own timestamp that can differ from the commit they point to. The response includes each PR's labels, merge time, and merge commit SHA — everything version aggregation needs.

The pipeline authenticates with a GitHub PAT stored as a secret in Jenkins/ADO. This is a standard pattern: both platforms routinely call external HTTP APIs from pipeline steps.

- Works with squash merges, merge commits, and rebase merges equally
- No dependency on commit message format
- Merge strategy becomes a team preference, not a pipeline constraint

- Pros: Merge-strategy agnostic. Most robust long-term. Straightforward HTTP call from any pipeline host.
- Cons: Requires a GitHub PAT with `repo` read scope stored as a pipeline secret. One additional API call per merge pipeline run.

> **Note on Option C (commit message parsing):** A third approach — parsing PR numbers out of GitHub merge commit messages (`Merge pull request #1234`) — was considered but rejected. It is fragile (couples the pipeline to GitHub's commit message format), does not work with rebase merges, and provides no advantage over Option B which uses the same API credential with better reliability.

> **Concurrency note:** Pipeline serialization on `main` (queue, don't cancel or run in parallel) is required regardless of which option is chosen. This is a universal prerequisite and is documented in the Prerequisites section of the strategy doc. The specific mechanism varies by platform (GitHub Actions concurrency groups, Jenkins `lock` step, ADO concurrency limit) and is addressed in Change 5.

---

## ✅ Change 4 — AI Classification Is an External Service *(applied)*

**What changes:**
The current strategy embeds the AI call directly in the pipeline using `curl` to GitHub Models. This is incompatible with org policy (AI not permitted on build machines) and is tightly coupled to a specific AI provider.

**Proposed behavior:**
AI classification is moved behind a service boundary. The pipeline calls a defined HTTP endpoint and receives a structured response. The pipeline does not know or care whether AI, rules-based logic, or a human is on the other side.

**Service contract (input):**

```json
{
  "title": "string",
  "description": "string",
  "diff_stat": "string",
  "diff_content": "string | null"
}
```

**Service contract (output):**

```json
{
  "classification": "major | minor | patch | unknown",
  "rationale": "string"
}
```

**Pipeline behavior:**
1. Pipeline POSTs to the classification service endpoint
2. If `classification` is not `unknown`, pipeline applies the returned label
3. If `classification` is `unknown`, pipeline applies `semver: unknown` and the label gate blocks the merge until a human overrides

**Service location:**
The service is external to the build machine. It can be:
- A dedicated microservice (could be `aidev-prompts` or a new service)
- An Azure Function or similar
- Any HTTP endpoint the pipeline can reach

The endpoint URL and auth token are stored as pipeline secrets. Swapping AI providers or turning AI off entirely requires only a config change, not a pipeline change.

---

## ✅ Change 5 — Pipeline-Agnostic Strategy (Remove GitHub Actions Specifics) *(applied)*

**What changes:**
The current document contains GitHub Actions YAML, GitHub-specific concepts (GitHub Models token, `${{ secrets.GITHUB_TOKEN }}`), and references to GitHub Rulesets. The strategy needs to work with ADO Pipelines or Jenkins.

**Proposed behavior:**
The strategy document describes *what* the pipeline must do, not *how* to implement it in a specific platform. Each concept maps generically:

| Current (GitHub-specific) | Generic equivalent |
|---|---|
| GitHub Actions workflow | ADO Pipeline / Jenkinsfile / any CI trigger |
| `${{ secrets.GITHUB_TOKEN }}` | Pipeline secret variable |
| GitHub Ruleset required status check | ADO Branch Policy required build |
| GitHub PR labels | ADO PR labels / custom tags (see note below) |
| GitHub Models `models.inference.ai.azure.com` | External classification service (see Change 4) |

**Note on PR labels in ADO:**
ADO does not have native PR labels equivalent to GitHub's. Two workarounds:
- **Option A** — Use ADO PR custom properties or PR description tags (e.g., a `[semver: minor]` token in the description)
- **Option B** — Use an external label store (e.g., a JSON file committed to a branch, or a simple database keyed by PR ID)
- **Option C** — Enforce via vote/approval: map `semver:` values to required reviewer approvals (blunt but native to ADO)

The preferred option depends on what infrastructure already exists. This is an open question to resolve before implementation.

---

## Summary of Changes

| # | Change | Impact |
|---|---|---|
| ✅ 1 | Labels are enforced; AI is optional | Applied |
| ✅ 2 | Package-level versioning | Applied (incl. Prerequisites split for library/service/multi-package repos) |
| ✅ 3 | Merge strategy — GitHub PR API (Option B) | Applied (squash requirement removed; version aggregation uses `merged_at` via GitHub REST API) |
| ✅ 4 | AI behind a service boundary | Applied (HTTP service contract; endpoint/auth as pipeline secrets; no GitHub Models reference) |
| ✅ 5 | Remove GitHub Actions specifics | Applied (no YAML, no `GITHUB_TOKEN`, no Rulesets; GitHub Actions listed as one of three serialization options alongside Jenkins and ADO) |
