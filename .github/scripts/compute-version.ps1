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

# Returns $true if the commit is already contained in the tag, $false if it is
# not, and $null if the commit is unknown to this clone (caller should fall back).
function Test-CommitInTag {
    param(
        [string]$CommitOid,
        [string]$Tag
    )

    if (-not $CommitOid -or -not $Tag) {
        return $null
    }

    # A non-zero exit from git is an expected answer here, not a failure, so
    # stop PowerShell 7.4+ from turning exit 1 into a terminating error.
    $PSNativeCommandUseErrorActionPreference = $false
    git merge-base --is-ancestor $CommitOid $Tag 2>$null

    switch ($LASTEXITCODE) {
        0 { return $true }
        1 { return $false }
        default {
            Write-Host "  merge-base check for $CommitOid returned exit $LASTEXITCODE; using timestamp fallback"
            return $null
        }
    }
}

$allPRs = gh pr list --repo $env:GITHUB_REPOSITORY --state merged --base main --limit 1000 --json number,labels,mergedAt,mergeCommit | ConvertFrom-Json

# Decide which merged PRs belong to the next release window.
#
# A PR counts when its merge commit is NOT already contained in the latest
# release tag. Containment is exact and immune to the sub-second drift between
# a merge commit's committer date and GitHub's mergedAt timestamp, which used
# to let the just-released PR slip back into the next window and inflate the
# bump (issue #128).
#
# The timestamp comparison is kept only as a fallback: when there is no release
# tag yet, or when a PR has no recorded merge commit (for example, its branch
# was deleted long ago and the commit is not in this clone).
$mergedPRs = @($allPRs | Where-Object {
    if ($null -eq $_.mergedAt) {
        return $false
    }

    if ($latestTag) {
        $contained = Test-CommitInTag -CommitOid $_.mergeCommit.oid -Tag $latestTag
        if ($contained -eq $true) {
            return $false
        }
        if ($contained -eq $false) {
            return $true
        }
    }

    $_.mergedAt -gt $taggedAt
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
