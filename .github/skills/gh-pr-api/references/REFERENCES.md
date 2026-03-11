# GitHub PR API — Extended Reference

Supplementary material for `github-pr-api` skill. Load this file when you need filtering/sorting patterns, thread state details, comment body handling, caching, error handling, or best practices.

## Filtering and Sorting Threads

### Common Filters

**Unresolved threads only:**
```powershell
$unresolved = $threads | Where-Object { -not $_.isResolved }
```

**Code-related threads (exclude general comments):**
```powershell
$codeThreads = $threads | Where-Object { $_.path -ne $null }
```

**Non-outdated threads (code still exists):**
```powershell
$current = $threads | Where-Object { -not $_.isOutdated }
```

**Combine filters:**
```powershell
$activeCodeThreads = $threads | Where-Object { 
    -not $_.isResolved -and 
    $_.path -ne $null -and 
    -not $_.isOutdated 
}
```

### Sorting Threads

**By file path and line number:**
```powershell
# Use line if available, fallback to originalLine
$sorted = $threads | Sort-Object path, @{
    Expression = { 
        if ($_.line) { $_.line } else { $_.originalLine }
    }
}
```

**By creation date (oldest first):**
```powershell
$sorted = $threads | Sort-Object @{
    Expression = { $_.comments.nodes[0].createdAt }
}
```

**Priority: non-outdated first, then by file/line:**
```powershell
$sorted = $threads | Sort-Object `
    @{Expression = { $_.isOutdated }; Ascending = $true}, `
    path, `
    @{Expression = { if ($null -ne $_.line) { $_.line } else { $_.originalLine } }}
```

## Thread Resolution States

### Understanding `isResolved`

**Thread-level property:**
- `isResolved: false` — Thread is active/unresolved
- `isResolved: true` — Thread has been marked resolved

**How threads get resolved:**
- Reviewer clicks "Resolve conversation" button
- Author resolves via GitHub UI
- Programmatic mutation (see `resolveReviewThread` in SKILL.md)

**Important:** Individual comments don't have resolved state — only threads do.

### Understanding `isOutdated`

**Indicates code has changed since the comment was placed:**
- `isOutdated: false` — Comment still applies to current code
- `isOutdated: true` — Code at that location has changed since comment

**When threads become outdated:**
- File is modified in subsequent commits
- Lines are added/removed shifting the context
- File is deleted or renamed

**Best practice:** Show outdated threads separately or warn the user; use `originalLine` for reference.

## Working with Comment Bodies

### Extracting First Comment

The main review feedback is usually in the first comment of a thread:

```powershell
$thread = $threads[0]
$primaryComment = $thread.comments.nodes[0]

Write-Host "Reviewer: $($primaryComment.author.login)"
Write-Host "Comment: $($primaryComment.body)"
Write-Host "Date: $($primaryComment.createdAt)"
```

### Comment Body Formats

Comments are markdown-formatted and may contain:

- **Code suggestions** — fenced code blocks with `suggestion` language tag
- **Issue descriptions** — plain prose explaining a problem
- **Questions** — rhetorical or clarifying questions

Parse the `body` string as markdown; no special API field separates these types.

## Caching Thread Data

### When to Cache

**Cache to disk if:**
- More than 3 threads
- User will navigate threads over time
- Need to preserve state across commands

**Keep in memory if:**
- 3 or fewer threads
- Single-use query
- Immediate processing

### Cache Location

**CRITICAL: Never write to repository workspace.** Use the OS temp directory:

**Windows:**
```powershell
$cacheDir = $env:TEMP
$cachePath = Join-Path $cacheDir "pr_${number}_threads.json"
$threads | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 $cachePath
```

**macOS/Linux:**
```bash
cacheDir="${TMPDIR:-/tmp}"
cachePath="${cacheDir}/pr_${number}_threads.json"
printf '%s' "$threadsJson" > "$cachePath"
```

### Cache Naming Convention

```
pr_{number}_threads.json          # All threads
pr_{number}_unresolved.json       # Unresolved only
pr_{number}_{owner}_{repo}.json   # With repo context
```

## Error Handling

### Common Errors

**Authentication expired:**
```
gh: reauthenticate required
```
**Solution:** `gh auth refresh`

**Invalid PR number:**
```
Could not resolve to a PullRequest with the number 999999
```
**Solution:** Verify PR number and repo context.

**Rate limiting:**
```
API rate limit exceeded
```
**Solution:** Wait or use authenticated requests (higher limit).

**Network errors:**
```
failed to run gh: exit code 1
```
**Solution:** Check network connection, verify enterprise hostname (`$env:GH_HOST`).

**GraphQL parse error (`RCURLY`):**
```
Parse error on "}" (RCURLY) at [1, N]
```
**Solution:** Unbalanced braces in query string. Switch to the `--input -` JSON body pattern from SKILL.md to avoid PowerShell escaping issues.

### Defensive Practices

**Always check auth before operations:**
```powershell
$authStatus = gh auth status 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "GitHub CLI not authenticated. Run: gh auth login"
    return
}
```

**Validate PR before querying:**
```powershell
# gh pr view does NOT accept --hostname; set GH_HOST for enterprise
if ($prUrl -match 'github\.docusignhq\.com') {
    $env:GH_HOST = 'github.docusignhq.com'
}
try {
    $pr = gh pr view $number --repo "$owner/$repo" --json number,title 2>&1 | ConvertFrom-Json
    if (-not $pr) { throw "PR not found" }
} catch {
    Write-Error "Cannot access PR #${number}: $_"
    return
}
```

**Handle empty results gracefully:**
```powershell
$unresolvedThreads = @($threads | Where-Object { -not $_.isResolved })
if ($unresolvedThreads.Count -eq 0) {
    Write-Host "✅ No unresolved review threads found!"
    return
}
```

## Best Practices

### Query Efficiency

- Use pagination cursors, not offset-based pagination
- Request only needed fields in GraphQL
- Always include `id` in thread node fields (required for `resolveReviewThread`)
- Cache results when navigating multiple threads
- Batch operations when possible

### User Experience

- Show progress for long operations
- Provide clear error messages with remediation steps
- Validate prerequisites (auth, repo access) before starting
- Display thread counts before navigation

### Data Handling

- Use `@()` array wrapper for reliable counting in PowerShell
- Handle null values (`line` may be null if outdated; use `originalLine` as fallback)
- Preserve original data for debugging
- Use structured objects, not plain text parsing
