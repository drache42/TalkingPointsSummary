# Release Tagging

This repository separates build identity from release identity.

- Build identity comes from pushes to `main`: `main` and `sha-<shortsha>` image tags
- Release identity comes from a manual git tag in the format `v<major>.<minor>.<patch>`

When a valid release tag is pushed, the main pipeline promotes the already-published SHA-based images to stable release tags instead of rebuilding them.

## Published Image Tags

For a release tag like `v1.2.3`, the workflow promotes images to:

- `1.2.3`
- `1.2`
- `latest`

The release tag must point to a commit that is contained in `origin/main`. When using `Publish-ReleaseTag.ps1`, the script additionally enforces that the tag is created at the latest `origin/main` commit.

## Versioning Rules

Use semantic versioning conservatively:

- `MAJOR`: incompatible changes to operator-visible behavior such as configuration keys, CLI contracts, image behavior, or deployment expectations
- `MINOR`: backward-compatible new features
- `PATCH`: backward-compatible fixes and security updates

Valid release tags must match:

```text
v<major>.<minor>.<patch>
```

Examples:

- `v1.0.0`
- `v1.2.3`
- `v2.4.1`

Invalid examples:

- `1.0.0`
- `v1.0`
- `v1.0.0-beta`

## Script

Use [Publish-ReleaseTag.ps1](d:/Code/TalkingPointsSummary/scripts/Publish-ReleaseTag.ps1) to validate and publish a release tag.

The script does the following:

1. Fetches `origin/main` and all tags
2. Shows the current branch, current commit, and `origin/main` commit
3. Warns if your current checkout is not the same commit as the current `origin/main` tip
4. Verifies that the current commit exactly matches the latest `origin/main` commit
5. Shows up to 3 tags for each of the 3 newest major versions
6. Asks whether to continue
7. Prompts for the release tag
8. Verifies the tag format
9. Verifies that the tag does not already exist locally or on origin
10. Shows the exact commands it will run
11. Asks for final confirmation
12. Creates and pushes the annotated tag

## Dry Run

To validate everything without creating or pushing a tag:

```powershell
.\scripts\Publish-ReleaseTag.ps1 -DryRun
```

This follows the same validation and prompting flow, but stops before any git changes are made.

## Real Run

To create and push a real release tag:

```powershell
.\scripts\Publish-ReleaseTag.ps1
```

## Typical Release Flow

1. Merge the release candidate commit into `main`
2. Wait for the `main` pipeline to publish `sha-<shortsha>` images
3. Run the script in dry-run mode and confirm the target commit and chosen version
4. Run the script normally and push the release tag
5. Watch the `Promote release tags` job in GitHub Actions

## Prerequisites

- `git` must be installed and available in `PATH`
- You must be in this repository when running the script
- Your git remote must be named `origin`
- Your current `HEAD` must match `origin/main`
- You must have permission to push tags to `origin`

## Failure Cases

The script stops without creating a tag if:

- the current commit is not the latest `origin/main` commit
- the proposed tag does not match `v<major>.<minor>.<patch>`
- the tag already exists locally
- the tag already exists on `origin`
- you decline either confirmation prompt
