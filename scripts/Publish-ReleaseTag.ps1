<#
.SYNOPSIS
Validates and publishes a release tag for the current commit.

.DESCRIPTION
This script implements the repository's release-tag workflow for container image
promotion. It fetches origin and tags, compares the current commit to origin/main,
verifies that the current commit exactly matches the latest origin/main commit,
summarizes recent semantic version tags, validates a new tag in
v<major>.<minor>.<patch> format, and then creates and pushes an annotated git
tag.

Use -DryRun to exercise the same validation flow without creating or pushing a
tag.

.PARAMETER DryRun
Runs the full validation and confirmation flow without creating or pushing a tag.

.EXAMPLE
.\scripts\Publish-ReleaseTag.ps1 -DryRun

Fetches origin state, validates the current commit, asks for a proposed release
tag, and stops before creating or pushing anything.

.EXAMPLE
.\scripts\Publish-ReleaseTag.ps1

Runs the interactive release flow and, after confirmation, creates and pushes an
annotated tag such as v1.2.3.

.NOTES
Release tags in this repository must:
- Match v<major>.<minor>.<patch>
- Point to the latest commit on origin/main
- Be unique locally and on origin
#>
[CmdletBinding()]
param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = & git @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $message = ($output | Out-String).Trim()
        throw "git $($Arguments -join ' ') failed. $message"
    }

    return $output
}

function Test-Yes {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ($null -eq $Value) {
        return $false
    }

    switch -Regex ($Value.Trim()) {
        '^(y|yes)$' { return $true }
        default { return $false }
    }
}

function Get-VersionedTags {
    # Only consider tags that match the repository's release semver format.
    $pattern = '^v(?<Major>\d+)\.(?<Minor>\d+)\.(?<Patch>\d+)$'

    $tags = Invoke-Git -Arguments @('tag', '--list', 'v*.*.*')
    $versionedTags = foreach ($tag in $tags) {
        if ($tag -match $pattern) {
            [pscustomobject]@{
                Name = $tag.Trim()
                Major = [int]$Matches.Major
                Minor = [int]$Matches.Minor
                Patch = [int]$Matches.Patch
            }
        }
    }

    return $versionedTags |
        Sort-Object -Property @{ Expression = 'Major'; Descending = $true }, @{ Expression = 'Minor'; Descending = $true }, @{ Expression = 'Patch'; Descending = $true }, @{ Expression = 'Name'; Descending = $true }
}

function Show-TagSummary {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$VersionedTags
    )

    if (-not $VersionedTags -or $VersionedTags.Count -eq 0) {
        Write-Host 'Existing release tags: none found.' -ForegroundColor Yellow
        return
    }

    $majorVersions = $VersionedTags |
        Select-Object -ExpandProperty Major -Unique |
        Sort-Object -Descending |
        Select-Object -First 3

    Write-Host 'Recent release history:' -ForegroundColor Cyan
    foreach ($major in $majorVersions) {
        $tagsForMajor = $VersionedTags |
            Where-Object { $_.Major -eq $major } |
            Select-Object -First 3

        $tagList = ($tagsForMajor | Select-Object -ExpandProperty Name) -join ', '
        Write-Host ("  Major {0}: {1}" -f $major, $tagList)
    }
}

function Assert-ValidVersionTag {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TagName
    )

    if ($TagName -notmatch '^v\d+\.\d+\.\d+$') {
        throw 'The tag must match v<major>.<minor>.<patch>, for example v1.0.0.'
    }
}

# Fetch first so all checks use current remote state instead of stale local refs.
Write-Host 'Fetching origin and tags...' -ForegroundColor Cyan
Invoke-Git -Arguments @('fetch', 'origin', 'main', '--tags', '--prune') | Out-Null

$currentBranch = (Invoke-Git -Arguments @('rev-parse', '--abbrev-ref', 'HEAD') | Select-Object -First 1).Trim()
$headSha = (Invoke-Git -Arguments @('rev-parse', 'HEAD') | Select-Object -First 1).Trim()
$originMainSha = (Invoke-Git -Arguments @('rev-parse', 'origin/main') | Select-Object -First 1).Trim()

Write-Host ''
Write-Host 'Release target:' -ForegroundColor Cyan
Write-Host ("  Current branch : {0}" -f $currentBranch)
Write-Host ("  Current commit : {0}" -f $headSha)
Write-Host ("  origin/main    : {0}" -f $originMainSha)

if ($headSha -ne $originMainSha) {
    Write-Warning 'Your current checkout is not the same commit as origin/main. You are looking at a different commit than the current main tip.'
    throw 'Release tags must point to the latest commit on origin/main. Check out the current main tip before continuing.'
}

Write-Host ''
Show-TagSummary -VersionedTags (Get-VersionedTags)

Write-Host ''
Write-Host 'Planned action:' -ForegroundColor Cyan
Write-Host ("  Create an annotated git tag on commit {0}" -f $headSha)
Write-Host '  Validate that the tag matches v<major>.<minor>.<patch>'
if ($DryRun) {
    Write-Host '  Dry run only: do not create or push the tag' -ForegroundColor Yellow
}
else {
    Write-Host '  Push the tag to origin'
}

$continueResponse = Read-Host 'Continue to version selection? [y/N]'
if (-not (Test-Yes -Value $continueResponse)) {
    Write-Host 'Cancelled. No tag was created.' -ForegroundColor Yellow
    return
}

$tagName = (Read-Host 'Enter the release tag (example: v1.0.0)').Trim()
Assert-ValidVersionTag -TagName $tagName

$existingLocalTag = & git rev-parse --verify --quiet "refs/tags/$tagName"
if ($LASTEXITCODE -eq 0) {
    throw "Tag $tagName already exists locally."
}

$remoteTagSha = Invoke-Git -Arguments @('ls-remote', '--tags', 'origin', "refs/tags/$tagName")

if (-not [string]::IsNullOrWhiteSpace(($remoteTagSha | Out-String))) {
    throw "Tag $tagName already exists on origin."
}

Write-Host ''
Write-Host 'About to run:' -ForegroundColor Cyan
Write-Host ("  git tag -a {0} {1} -m `"Release {0}`"" -f $tagName, $headSha)
if ($DryRun) {
    Write-Host ("  git push origin {0}" -f $tagName)
    Write-Host '  Dry run will stop before executing these commands.' -ForegroundColor Yellow
}
else {
    Write-Host ("  git push origin {0}" -f $tagName)
}

$pushResponse = Read-Host 'Create and push this tag? [y/N]'
if (-not (Test-Yes -Value $pushResponse)) {
    Write-Host 'Cancelled. No tag was created.' -ForegroundColor Yellow
    return
}

if ($DryRun) {
    Write-Host ''
    Write-Host ("Dry run complete. Tag {0} is valid for commit {1}, and no changes were made." -f $tagName, $headSha) -ForegroundColor Green
    return
}

Invoke-Git -Arguments @('tag', '-a', $tagName, $headSha, '-m', "Release $tagName") | Out-Null
try {
    Invoke-Git -Arguments @('push', 'origin', $tagName) | Out-Null
}
catch {
    & git tag -d $tagName | Out-Null
    throw
}

Write-Host ''
Write-Host ("Created and pushed tag {0} for commit {1}." -f $tagName, $headSha) -ForegroundColor Green