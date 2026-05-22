# Versioning Strategy

## 1. Overview

This document describes the versioning strategy for C#/.NET repositories hosted on GitHub. It defines how pull requests are classified, how versions are computed and stamped into binaries, and how releases are triggered automatically when work merges to `main`.

The strategy solves two problems common in multi-repo ecosystems:

- **Versioning correctness** — every published artifact carries a meaningful, trustworthy [SemVer 2.0](https://semver.org/) version derived from the actual nature of the change, not a timestamp or build number.
- **Churn reduction** — in repositories with multiple packages, only packages that actually changed receive a new version, reducing unnecessary downstream dependency updates.

---

## 2. Core Concepts

Three foundational ideas underpin the system. Understanding these makes the rest of the document straightforward.

**The PR label is the enforced artifact.**
Every PR must carry exactly one `semver: major`, `semver: minor`, `semver: patch`, or `skip-release` label before it can merge. The label may be applied by a human, a bot, or an automated classification service — the pipeline does not care which. The label gate (a required status check) blocks the merge button until a qualifying label is present.

**The PR is the unit of work.**
Version aggregation operates at the PR level, not the commit level. When a release is triggered, the pipeline reads the labels of all PRs merged since the last release tag and takes the highest one. This is why direct pushes to `main` are prohibited — they have no label and would break aggregation.

**The git tag is the release record.**
Every release produces exactly one annotated git tag (e.g., `v1.3.0` or `Package.Core/v1.3.0`). The tag is the stable reference that downstream consumers, the merge pipeline, and CI all anchor to. Tags are immutable once written.

---

## 3. Scope

### Applies To

- **C#/.NET repositories** — DLL version stamping, `dotnet publish`/`dotnet pack`, and NuGet packaging are all .NET-specific. Applying this strategy to other languages requires replacing the [Version Stamping](#version-stamping) section of the Appendix with a language-appropriate equivalent.
- **Repositories hosted on GitHub** — version aggregation uses the GitHub REST PR API and GitHub PR labels as the semver metadata carrier.
- **Single integration branch (`main`)** — the pipeline tracks versions against one branch only.

### Out of Scope

- Parallel release trains
- Maintenance branches (e.g., `release/1.x`)
- Hotfix flows targeting older tags
- Non-.NET repositories without modification

---

## 4. Pipeline Design

There are three pipelines with distinct responsibilities running at different times.

| Pipeline | Trigger | Responsibility |
|---|---|---|
| **Label gate** | PR opened; any label change | Enforce that a qualifying semver label is present; block merge if not |
| **Classification pipeline** | PR opened; every subsequent push | Call the classification service and apply the returned label *(optional)* |
| **Merge pipeline** | PR merges to `main` | Compute version bump; write tag; trigger release |

> **Pipeline separation model:** The *merge pipeline* (one per repo) computes versions and writes tags. The *release pipeline* (one per package for libraries; one per repo for services) builds and publishes. This keeps version logic centralized while allowing packages to build and ship independently.

### 4.1 Label Gate

**Trigger:** PR opened; any label add or remove.

Performs a single check: does a qualifying `semver: major`, `semver: minor`, `semver: patch`, or `skip-release` label exist on the PR?

- **Pass** → merge button is enabled
- **Fail** → merge button is disabled

Declared as a **required status check** in branch protection — the platform enforces it at the infrastructure level. `semver: unknown` does not pass. See [Section 4.4 — Labels](#44-labels) for the full label definitions.

### 4.2 Classification Pipeline *(Optional)*

**Trigger:** PR opened; every subsequent push.

This pipeline is only needed if automated classification is configured. If teams prefer to apply labels manually, this pipeline is not required — the label gate operates independently of it.

On every push, the pipeline calls an external classification service and applies the returned label. A human can override the label at any time; the next push will re-evaluate and reset it. The pipeline does not know or care what is behind the endpoint; the endpoint URL and auth token are stored as pipeline secrets.

**Request:**
```json
{
  "title": "<PR title>",
  "description": "<PR body>",
  "diff_stat": "<git diff --stat output>",
  "diff_content": "<full diff>"
}
```

**Response:**
```json
{
  "classification": "major|minor|patch|unknown",
  "rationale": "1–3 sentence explanation"
}
```

The pipeline POSTs to the endpoint and reads the `classification` field from the response.

### 4.3 Merge Pipeline

**Trigger:** Every PR merge to `main`, regardless of label. The `skip-release` label does not prevent the pipeline from running — it is handled in Step 1.

#### Step 1 — Skip Check
If the merged PR carries the `skip-release` label → stop. No tag, no version bump, no publish.

#### Step 2 — Determine Affected Packages

**Single-package repos:** one implicit package — skip to Step 3.

**Multi-package repos:** Determine which packages were affected by the changes in this PR and run the remaining steps for each affected package.

> **Note:** Affected package detection is repo-specific and must be implemented as part of the pipeline setup for each multi-package repo. Edge cases such as shared files that affect multiple packages need to be handled explicitly.

#### Step 3 — Version Aggregation

1. Get the commit timestamp of the last tag:
   ```bash
   git log -1 --format=%cI <last-tag>   # ISO 8601 timestamp of the tagged commit
   ```
   > **Note:** Use the *tagged commit's* timestamp, not the tag object's creation time. Annotated tags have their own creation timestamp that can differ from the commit they point to. The commit timestamp is the correct baseline.

2. Call the GitHub PR API, sorted by `updated_at` descending:
   ```
   GET https://api.github.com/repos/{owner}/{repo}/pulls
       ?state=closed&base=main&sort=updated&direction=desc&per_page=100
   ```
   Authenticate with the pipeline's GitHub PAT (`Authorization: Bearer <token>`).

3. Page through results collecting PRs where `merged_at > <tagged-commit-timestamp>`. Stop when `updated_at ≤ <tagged-commit-timestamp>` — no PR on any subsequent page can have a `merged_at` after the threshold. This bounds the scan to PRs merged since the last release, not the full lifetime history.

4. For each collected PR, read its `semver: *` label
5. If any PR has **no** `semver: *` label → **fail the pipeline** (last-resort safety net — should never trigger because the label gate blocks merging without a label)
6. Take the **highest** label across all collected PRs (`major` beats `minor` beats `patch`)

#### Step 4 — Safety Check (Optional)

PR labels are applied one PR at a time, in isolation. The version bump from Step 3 reflects the highest label across individual PRs — but the *combined* diff since the last release may tell a different story. Two `patch` PRs together could constitute a `minor` change; a `skip-release` PR could have quietly introduced a breaking change.

If a [classification service](#42-classification-pipeline-optional) is configured, run it against the full combined diff since the last tag. If the service returns a bump **higher** than the aggregated label → emit a warning in the CI log. This does not block the release; it surfaces a potential under-classification for a human to review.

> **⚠️ Open question:** The right response when the full diff disagrees with the aggregated labels needs more deliberation. Options range from warn-only (current), to blocking the release and requiring a human to re-label the relevant PRs, to automatically upgrading the version bump. The tradeoffs involve pipeline friction, trust in the classification service, and how often legitimate disagreements occur in practice. This should be decided before implementing the safety check.

#### Step 5 — Tag and Trigger Release

1. Read the last tag for this package, compute the next version according to the aggregated bump
2. Create an annotated git tag:
   - Single-package: `git tag -a v1.3.0 HEAD -m "Release v1.3.0"`
   - Multi-package: `git tag -a Package.Core/v1.3.0 HEAD -m "Release Package.Core v1.3.0"`
3. Push tag to origin → triggers the package's dedicated release pipeline, which handles build and publish

### 4.4 Labels

#### Label Definitions

Every PR receives exactly one of the following GitHub labels:

| Label | Color | Meaning |
|---|---|---|
| `semver: major` | Red | Breaking change to an existing deployment |
| `semver: minor` | Yellow | New backward-compatible functionality |
| `semver: patch` | Green | Bug fix, maintenance, or no behavioral change |
| `semver: unknown` | Orange | Classification could not be determined — a human must apply a concrete label before merging |
| `skip-release` | — | PR merges without producing a release; no tag, no version bump |

#### SemVer Rules

This strategy follows [Semantic Versioning 2.0.0](https://semver.org/). The three labels map directly to the spec:

- **`semver: major`** — incompatible API or behavioral change (existing deployments may break)
- **`semver: minor`** — new functionality added in a backward-compatible way
- **`semver: patch`** — backward-compatible bug fix; default when no major or minor signals are present

#### Label Gate

See [Section 4.1 — Label Gate](#41-label-gate) for trigger, pass/fail behaviour, and enforcement details.

#### Skip-Release

Add `skip-release` before merging. Code still ships in the next real release. The merge pipeline runs an optional safety check that warns if `skip-release` changes inflated the actual diff level beyond what the non-skip PR labels indicate.

---

## 5. Setup

Work through this section in order when activating the strategy on a new repository.

### 5.1 Confirm Prerequisites

#### All Repositories

| Requirement | Why it matters |
|---|---|
| **C#/.NET repository** | DLL version stamping, `dotnet publish`/`dotnet pack`, and NuGet packaging are all .NET-specific. |
| **Hosted on GitHub** | Version aggregation uses the GitHub REST PR API and GitHub PR labels. |
| **Single integration branch (`main`)** | The pipeline tracks versions against one branch only. Parallel release trains and maintenance branches are out of scope. |
| **No direct pushes to `main`** — all changes via PRs | PRs are the unit of work that receives semver labels. A direct push bypasses the label gate and breaks version aggregation. |
| **Branch protection enforces required status checks** — merge button blocked when required checks fail | The label gate only works if the platform blocks merges on failure. |
| **`label-gate` configured as a required status check** | Without this, the label gate pipeline runs but cannot block the merge button. |
| **Version tags are protected** — only the pipeline service account can push or delete tags | If arbitrary users can push or delete tags, the version history can be corrupted. |
| **At least one bootstrap tag exists** before automation activates | The merge pipeline reads the last tag to compute the next version. With no tag, it has no baseline. See [Bootstrap](#53-bootstrap-version-tags). |
| **GitHub PAT with `repo` read scope** available as a pipeline secret | Required to call the GitHub PR API during version aggregation. |
| **The merge pipeline is serialized on `main`** — concurrent runs must queue, not run in parallel or cancel | Version aggregation reads the last tag as its baseline. If two merge pipelines run simultaneously, both read the same last tag and compute the same next version, causing a tag collision. |

> **Platform-specific serialization:** GitHub Actions: `concurrency:` group with `cancel-in-progress: false`. Jenkins: `throttleConcurrentBuilds` plugin or `lock` step. ADO: pipeline concurrency limit = 1.

#### Library Repositories (NuGet)

| Requirement | Why it matters |
|---|---|
| **Project files configured for `dotnet pack`** | Version is stamped at pack time via `/p:Version=`. |
| **NuGet feed available with pipeline publish rights** | The release pipeline pushes the packed `.nupkg`. |

#### Service Repositories (Docker)

| Requirement | Why it matters |
|---|---|
| **`Dockerfile` present at a known path** | The release pipeline invokes `docker build`. |
| **Dockerfile accepts `VERSION` and `COMMIT_SHA` build arguments** | Version stamping is injected via build args. A Dockerfile that ignores these args produces unstamped binaries. |
| **Container registry available with pipeline push rights** | The release pipeline tags and pushes the built image. |

#### Multi-Package Repositories

| Requirement | Why it matters |
|---|---|
| **Each package declares a root path** in a repo-level config file | The merge pipeline uses root paths to determine which packages a given PR touched. |
| **Each package has its own release pipeline** triggered by its package-scoped tag | The merge pipeline writes per-package tags (e.g., `Package.Core/v1.2.0`); a separate release pipeline per package watches for those tags. |
| **Each package has its own bootstrap tag** before automation activates | Same reason as single-package, but per-package. |

### 5.2 Configure Branch Protection on `main`

| Rule | Notes |
|---|---|
| No direct pushes — all changes via PRs | Prevents label gate bypass |
| Branch protection enforces required status checks (merge button blocked on failure) | Must block merge button |
| `label-gate` as required status check | Add after pipeline exists |
| Version tags protected | Only pipeline service account can push/delete |
| Merge pipeline serialized | Queue concurrent runs; see platform-specific config above |

All merge strategies (squash, merge commit, rebase) are acceptable. Version aggregation uses the GitHub PR API rather than git log, so merge method does not affect correctness.

### 5.3 Bootstrap Version Tags

The merge pipeline requires at least one existing tag per package before automation activates.

**Single-package repos:**
```bash
git tag -a v1.0.0 <sha> -m "Release v1.0.0"
git push origin v1.0.0
```

**Multi-package repos — one tag per package:**
```bash
git tag -a Package.Core/v1.0.0 <sha> -m "Release Package.Core v1.0.0"
git tag -a Package.Contracts/v1.0.0 <sha> -m "Release Package.Contracts v1.0.0"
git push origin --tags
```

Each package's version history is independent from the moment its bootstrap tag is created. If a package is already at a higher version, use its current version as the starting tag.

After bootstrapping, all tagging is fully automated.

### 5.4 Configure Multi-Package Registry *(Multi-package repos only)*

Create a repo-level config file declaring each package and its root path:

```json
{
  "packages": [
    { "name": "Package.Core", "path": "src/Package.Core/" },
    { "name": "Package.Contracts", "path": "src/Package.Contracts/" }
  ]
}
```

Each package also needs its own release pipeline watching for its scoped tag pattern (e.g., `Package.Core/v*`).

### 5.5 Verify Dockerfile *(Service repos only)*

Confirm the Dockerfile accepts `VERSION` and `COMMIT_SHA` build arguments and passes them into `dotnet publish`. See [Appendix — Version Stamping](#version-stamping) for service repository examples.

### 5.6 Configure Automated Classification *(Optional)*

If automated classification is desired, configure the classification service endpoint URL and auth token as pipeline secrets. See [Section 4.2 — Classification Pipeline](#42-classification-pipeline-optional) for the service contract.

---

## 6. Appendix

### Version Stamping

How a version is stamped into a built artifact is repo-specific. The merge pipeline produces a version string (e.g., `1.2.3`) and a commit SHA — what the release pipeline does with them depends on the repo's output type and existing build setup.

The examples below show one common approach for .NET repos, but repos are free to implement stamping differently as long as the version is traceable from the artifact back to the git tag.

#### Library Repositories (NuGet) — Example

```bash
dotnet pack /p:Version=1.2.3 /p:InformationalVersion=1.2.3+sha.abc1234f
```

#### Service Repositories (Docker) — Example

Version injected via Docker build arguments, which are passed into `dotnet publish` inside the image:

```dockerfile
ARG VERSION=0.0.0
ARG COMMIT_SHA=unknown
RUN dotnet publish \
  /p:Version=$VERSION \
  /p:InformationalVersion=$VERSION+sha.${COMMIT_SHA:0:8}
```

```bash
docker build \
  --build-arg VERSION=1.2.3 \
  --build-arg COMMIT_SHA=<full-sha> ...
```

This approach is preferred over tools like MinVer reading git inside Docker because `.git` is typically excluded from the build context.

#### .NET Assembly Version Attributes

For reference, the three standard .NET version attributes and their constraints:

| Attribute | Example | Notes |
|---|---|---|
| `AssemblyVersion` | `1.2.3.0` | Used by CLR binding; numeric only |
| `FileVersion` | `1.2.3.0` | Shown in Windows file Properties; numeric only |
| `InformationalVersion` | `1.2.3+sha.abc1234f` | Free-form; can carry SemVer 2.0 build metadata |

Only `InformationalVersion` can carry the commit SHA — the other two require a numeric-only format.

### Git Tag Formats

#### Single-Package Repositories

Git tags use the `v` prefix: `v<major>.<minor>.<patch>` (e.g. `v1.2.3`).

The `v` prefix is **not** part of SemVer 2.0 — it is a git tag naming convention. It exists to distinguish version tags from branch names, enable reliable glob filtering (`git tag -l 'v*'`), and match the dominant industry standard.

Stripping the `v` prefix:
```bash
TAG=v1.2.3
VERSION=${TAG#v}   # → 1.2.3
```

| Context | Format |
|---|---|
| Git tag | `v1.2.3` |
| Docker image tag | `1.2.3`, `1.2`, `latest` |
| `dotnet publish /p:Version=` | `1.2.3` |
| `AssemblyVersion` / `FileVersion` | `1.2.3.0` |
| `InformationalVersion` | `1.2.3+sha.abc1234f` |

#### Multi-Package Repositories

Tags are scoped to the package: `<PackageName>/v<major>.<minor>.<patch>` (e.g. `Package.Core/v2.1.0`).

The `/v` boundary makes tags glob-filterable per package: `git tag -l 'Package.Core/v*'`

Stripping the prefix:
```bash
TAG=Package.Core/v2.1.0
VERSION=${TAG#*/v}   # → 2.1.0
```

| Context | Format |
|---|---|
| Git tag | `Package.Core/v2.1.0` |
| NuGet package version | `2.1.0` |
| `dotnet publish /p:Version=` | `2.1.0` |
| `AssemblyVersion` / `FileVersion` | `2.1.0.0` |
| `InformationalVersion` | `2.1.0+sha.abc1234f` |

Packages within the same repo are versioned independently. A PR that touches only `Package.A` produces a new tag for `Package.A` only; `Package.B` is not re-tagged or re-published.

