$ErrorActionPreference = 'Stop'

$latestTag = git tag -l 'v*' --sort=-v:refname | Select-Object -First 1
Write-Host "Latest tag: $(if ($latestTag) { $latestTag } else { 'none' })"

if (-not $latestTag) {
    $major    = 1
    $minor    = 1
    $patch    = 2
    $taggedAt = '1970-01-01T00:00:00Z'
} else {
    $parts    = $latestTag.TrimStart('v').Split('.')
    $major    = [int]$parts[0]
    $minor    = [int]$parts[1]
    $patch    = [int]$parts[2]
    $taggedAt = git log -1 --format='%cI' $latestTag
}

Write-Host "Current version: $major.$minor.$patch (tagged at $taggedAt)"
$shortSha = $env:GITHUB_SHA.Substring(0, 7)

$allPRs = gh pr list --repo $env:GITHUB_REPOSITORY --state merged --base main --limit 1000 --json number,labels,mergedAt,mergeCommit | ConvertFrom-Json

# Decide which merged PRs belong to the next release window.
#
# The window is the set of commits reachable from this run's commit (HEAD ==
# GITHUB_SHA, the commit auto-release will tag) but not from the latest release
# tag: `git rev-list "$latestTag..HEAD"`. A PR counts when its merge commit is in
# that set. The set is enumerated once and membership is an in-memory lookup.
#
# This is exact and fixes two bugs a mergedAt-vs-tag-date comparison had:
#   - the just-released PR was re-counted, because GitHub records mergedAt a beat
#     after the merge commit's committer date (#128); and
#   - a PR merged while an earlier run was still in flight was counted in that
#     run and again in its own. Its merge commit is not reachable from the
#     earlier run's HEAD, yet the live `gh pr list` already returns it.
#
# The mergedAt-vs-tag-date comparison is kept only for PRs with no recorded merge
# commit. Both sides are parsed as DateTimeOffset, not compared as strings: gh
# reports mergedAt as '...Z' while git %cI uses a numeric offset, so a raw string
# compare mis-orders equal instants and is wrong on non-UTC runners.
$revListRange  = if ($latestTag) { "$latestTag..HEAD" } else { 'HEAD' }
$windowCommits = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(git rev-list $revListRange),
    [System.StringComparer]::OrdinalIgnoreCase)
Write-Host "Commits in release window ($revListRange): $($windowCommits.Count)"

$mergedPRs = @($allPRs | Where-Object {
    if ($null -eq $_.mergedAt) {
        return $false
    }

    $oid = $_.mergeCommit.oid
    if ($oid) {
        return $windowCommits.Contains($oid)
    }

    [datetimeoffset]$_.mergedAt -gt [datetimeoffset]$taggedAt
})

$prCount = $mergedPRs.Count
Write-Host "PRs in next release window: $prCount"

if ($prCount -eq 0) {
    Write-Host 'No PRs merged since last release.'
    "skip=true"                    | Add-Content -Path $env:GITHUB_OUTPUT
    "version=$major.$minor.$patch" | Add-Content -Path $env:GITHUB_OUTPUT
    "short_sha=$shortSha"          | Add-Content -Path $env:GITHUB_OUTPUT
    exit 0
}

$triggerPR      = $mergedPRs | Sort-Object mergedAt | Select-Object -Last 1
$triggerHasSkip = $triggerPR.labels | Where-Object { $_.name -eq 'skip-release' }

if ($triggerHasSkip) {
    Write-Host 'Triggering PR has skip-release; deferring release.'
    "skip=true"                    | Add-Content -Path $env:GITHUB_OUTPUT
    "version=$major.$minor.$patch" | Add-Content -Path $env:GITHUB_OUTPUT
    "short_sha=$shortSha"          | Add-Content -Path $env:GITHUB_OUTPUT
    exit 0
}

$hasMajor     = $false
$hasMinor     = $false
$hasPatch     = $false
$hasAnySemver = $false

foreach ($pr in $mergedPRs) {
    $labelNames = @($pr.labels | ForEach-Object { $_.name })
    if ($labelNames -contains 'semver: major') {
        $hasMajor = $true; $hasAnySemver = $true
    } elseif ($labelNames -contains 'semver: minor') {
        $hasMinor = $true; $hasAnySemver = $true
    } elseif ($labelNames -contains 'semver: patch') {
        $hasPatch = $true; $hasAnySemver = $true
    }
}

if (-not $hasAnySemver) {
    Write-Host 'No semver labels found among merged PRs; skipping release.'
    "skip=true"                    | Add-Content -Path $env:GITHUB_OUTPUT
    "version=$major.$minor.$patch" | Add-Content -Path $env:GITHUB_OUTPUT
    "short_sha=$shortSha"          | Add-Content -Path $env:GITHUB_OUTPUT
    exit 0
}

if ($hasMajor) {
    $newVersion = "$($major + 1).0.0"
} elseif ($hasMinor) {
    $newVersion = "$major.$($minor + 1).0"
} else {
    $newVersion = "$major.$minor.$($patch + 1)"
}

Write-Host "New version: $newVersion"
"skip=false"           | Add-Content -Path $env:GITHUB_OUTPUT
"version=$newVersion"  | Add-Content -Path $env:GITHUB_OUTPUT
"short_sha=$shortSha"  | Add-Content -Path $env:GITHUB_OUTPUT
