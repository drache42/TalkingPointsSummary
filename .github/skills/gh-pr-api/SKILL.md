
---
name: github-pr-api
description: Access GitHub pull request data using gh CLI and GraphQL API for review threads, comments, and PR metadata. Use when working with PR review analysis/navigation, comment workflows, or GraphQL querying. Do not use this skill to create/open a PR from branch changes.
---

# GitHub Pull Request API Access

Domain expertise for accessing and manipulating GitHub pull request data using the GitHub CLI (`gh`) and GraphQL API.

## Scope Boundary (Critical)

- This skill is for **existing PRs**: review threads, comments, metadata, and GraphQL queries.
- This skill is **not** for generating PR descriptions from branch diffs or creating/opening a PR from current branch.
- For "write PR description", "create PR", or "open PR" flows, use `pull-request-description-and-creation`.

## When to Use This Skill

- User needs to fetch PR review threads or comments
- User wants to resolve, query, or navigate review comments
- User asks about GitHub CLI authentication or setup
- User needs to parse PR URLs or identifiers
- User wants to filter PR data (resolved/unresolved threads, outdated comments)
- User asks about GraphQL queries for pull requests

## When NOT to Use This Skill

- User asks to create/open/make a PR from current branch
- User asks to generate PR title/body from `git diff`
- User asks for "describe my changes" before opening a PR

## GitHub CLI (`gh`) Setup

### Installation Check

**Always verify `gh` is installed and authenticated before PR operations.**

**Check version:**
```powershell
gh --version
```

**Check authentication:**
```powershell
gh auth status
```

### Installation Methods

**Windows:**
```powershell
# Chocolatey
choco install gh -y

# Winget
winget install --id GitHub.cli
```

**macOS:**
```bash
brew install gh
```

**Linux:**
See https://github.com/cli/cli/blob/trunk/docs/install_linux.md

### Authentication

**GitHub.com:**
```powershell
gh auth login
```

**GitHub Enterprise Server (e.g., github.docusignhq.com):**
```powershell
gh auth login
# Select: GitHub Enterprise Server
# Enter hostname: github.docusignhq.com
# Follow authentication flow (browser or token)
# Choose protocol: HTTPS or SSH
```

**CRITICAL — Enterprise hostname for all subsequent commands:**

Most `gh` subcommands (`gh pr view`, `gh api`, etc.) do **not** accept a `--hostname` flag — only `gh auth` and `gh api` do. The correct approach is to set the `GH_HOST` environment variable **once per terminal session** before issuing any `gh` command targeting the enterprise host:

```powershell
# Set once at the start of any session targeting github.docusignhq.com
$env:GH_HOST = 'github.docusignhq.com'

# All subsequent gh commands automatically target the enterprise host
gh auth status
gh pr view 1234 --repo Owner/Repo --json number,title
gh api graphql -f query='...' ...
```

**Verify access to specific repo:**
```powershell
$env:GH_HOST = 'github.docusignhq.com'   # required for enterprise
gh repo view {owner}/{repo}
```

## PR Identifier Parsing

### Supported Formats

Users can provide PR references in multiple formats:

| Format | Example | Parse To |
|--------|---------|----------|
| Full URL | `https://github.com/owner/repo/pull/123` | `owner/repo/123` |
| Enterprise URL | `https://github.docusignhq.com/Core/Core/pull/109448` | `Core/Core/109448` |
| Repo shorthand | `owner/repo#123` | `owner/repo/123` |
| Number only | `123` | Use current repo context |

### Parsing Logic

**From full URL:**
```powershell
# Extract owner, repo, number from URL
if ($prUrl -match 'github\.(com|docusignhq\.com)/([^/]+)/([^/]+)/pull/(\d+)') {
    $owner = $matches[2]
    $repo = $matches[3]
    $number = $matches[4]
}
```

**From shorthand:**
```powershell
# Parse owner/repo#123 format
if ($prRef -match '([^/]+)/([^#]+)#(\d+)') {
    $owner = $matches[1]
    $repo = $matches[2]
    $number = $matches[3]
}
```

**From number only:**
```powershell
# Get current repo context
$repoInfo = gh repo view --json owner,name | ConvertFrom-Json
$owner = $repoInfo.owner.login
$repo = $repoInfo.name
$number = $prNumber
```

## Review Threads vs Comments

### Critical Distinction

**Review Thread:**
- Container for a conversation
- Has `isResolved` status (thread-level)
- Has `isOutdated` status
- Associated with file path and line number
- Contains one or more comments

**Review Comment:**
- Individual message in a thread
- Has `databaseId` (used for replies)
- Has author, body, timestamp
- First comment in thread often contains the main feedback

### Data Model

```
ReviewThread
├── id: string (node ID — required for resolveReviewThread mutation)
├── isResolved: boolean (thread-level resolved state)
├── isOutdated: boolean (code has changed since comment)
├── path: string (file path, null for general comments)
├── line: int (current line number, null if outdated)
├── originalLine: int (line number when created)
└── comments: ReviewThreadCommentConnection
    └── nodes: [ReviewComment]
        ├── databaseId: int (used for REST API replies via in_reply_to)
        ├── body: string (markdown content)
        ├── author: { login: string }
        ├── createdAt: datetime
        └── ...
```

## Fetching Review Threads

### `-f` vs `-F` Flag Reference

`gh api graphql` uses two distinct flag styles — mixing them causes silent failures or type mismatch errors:

| Flag | Type | Use for |
|------|------|---------|
| `-f` | String | The `query` body and all `String!` variables (`owner`, `repo`, `after`) |
| `-F` | Typed (auto-detects int/bool) | `Int!` variables like `number` |

```powershell
# Correct flag usage:
gh api graphql `
  -f 'query=query(...){...}' `   # query body → -f (string)
  -f owner='MyOrg' `             # String! → -f
  -f repo='MyRepo' `             # String! → -f
  -F number=1234                 # Int!   → -F (typed)
```

### Reliable Query Pattern: JSON Body via `--input -`

**CRITICAL for complex queries in PowerShell:** Inline `-f query=` is unreliable for long GraphQL strings due to PowerShell quote-escaping rules. Use `ConvertTo-Json | gh api graphql --input -` instead — it composes the payload as structured JSON and bypasses escaping entirely:

```powershell
$env:GH_HOST = 'github.docusignhq.com'  # omit for github.com

$owner  = 'MyOrg'
$repo   = 'MyRepo'
$number = 1234

$gqlQuery = 'query($owner:String!,$repo:String!,$number:Int!){repository(owner:$owner,name:$repo){pullRequest(number:$number){reviewThreads(first:100){nodes{id isResolved isOutdated path line originalLine comments(first:50){nodes{databaseId body createdAt author{login}}}}}}}}'

$body = @{
    query     = $gqlQuery
    variables = @{ owner = $owner; repo = $repo; number = $number }
} | ConvertTo-Json -Depth 5 -Compress

$resp    = $body | gh api graphql --input - | ConvertFrom-Json
$threads = @($resp.data.repository.pullRequest.reviewThreads.nodes)
Write-Host "Fetched $($threads.Count) threads"
```

This pattern works reliably across PowerShell 5, 7, and VS Code integrated terminals.

### Single-Call Query (Recommended — no pagination needed)

**Use this when PR has fewer than 100 review threads (the common case).**

The `do/while` + heredoc pattern is unreliable in VS Code integrated terminals (PowerShell 5 and 7). Use the JSON body via `--input -` pattern (same as above):

```powershell
# Set for enterprise GitHub (required — most gh subcommands ignore --hostname)
$env:GH_HOST = 'github.docusignhq.com'  # omit for github.com

$owner  = 'MyOrg'
$repo   = 'MyRepo'
$number = 1234

$gqlQuery = 'query($owner:String!,$repo:String!,$number:Int!){repository(owner:$owner,name:$repo){pullRequest(number:$number){reviewThreads(first:100){pageInfo{hasNextPage endCursor}nodes{id isResolved isOutdated path line originalLine comments(first:50){nodes{databaseId body createdAt author{login}}}}}}}}'

$body = @{
    query     = $gqlQuery
    variables = @{ owner = $owner; repo = $repo; number = $number }
} | ConvertTo-Json -Depth 5 -Compress

$resp    = $body | gh api graphql --input - | ConvertFrom-Json
$threads = @($resp.data.repository.pullRequest.reviewThreads.nodes)
Write-Host "Fetched $($threads.Count) threads"
```

### Paginated Query (PowerShell)

**Use when a PR may have 100+ review threads.** To avoid the do/while + heredoc reliability issues, use a `while ($true)` loop with an explicit break, and assign the heredoc before the loop:

```powershell
$env:GH_HOST = 'github.docusignhq.com'  # omit for github.com

$owner  = 'MyOrg'
$repo   = 'MyRepo'
$number = 1234

# Define query once, outside the loop
$gqlQuery = 'query($owner:String!,$repo:String!,$number:Int!,$after:String){repository(owner:$owner,name:$repo){pullRequest(number:$number){reviewThreads(first:100,after:$after){pageInfo{hasNextPage endCursor}nodes{id isResolved isOutdated path line originalLine comments(first:50){nodes{databaseId body createdAt author{login}}}}}}}}'

$after  = $null
$threads = @()

while ($true) {
    $extraArgs = if ($after) { @('-f', "after=$after") } else { @() }

    $resp = gh api graphql `
        -f query="$gqlQuery" `
        -f owner="$owner" `
        -f repo="$repo" `
        -F number=$number `
        $extraArgs | ConvertFrom-Json

    $page    = $resp.data.repository.pullRequest.reviewThreads
    $threads += $page.nodes

    if (-not $page.pageInfo.hasNextPage) { break }
    $after = $page.pageInfo.endCursor
}

Write-Host "Total threads fetched: $($threads.Count)"
```

### Quick Unresolved Threads Query

**Minimal single-call query (metadata only, no comment bodies):**

```powershell
$env:GH_HOST = 'github.docusignhq.com'  # omit for github.com

$owner  = 'MyOrg'
$repo   = 'MyRepo'
$number = 1234

$gqlQuery = 'query($owner:String!,$repo:String!,$number:Int!){repository(owner:$owner,name:$repo){pullRequest(number:$number){reviewThreads(first:100){nodes{id isResolved isOutdated path line originalLine}}}}}'

$resp = gh api graphql `
  -f query="$gqlQuery" `
  -f owner="$owner" `
  -f repo="$repo" `
  -F number=$number | ConvertFrom-Json

# Filter to unresolved only
$unresolvedThreads = @(
    $resp.data.repository.pullRequest.reviewThreads.nodes |
    Where-Object { -not $_.isResolved }
)
Write-Host "Unresolved threads: $($unresolvedThreads.Count)"
```

See [references/REFERENCE.md](references/REFERENCE.md) for filtering, sorting, thread state explanations, comment body handling, and caching patterns.

## Thread Resolution

### resolveReviewThread Mutation

Use the `resolveReviewThread` GraphQL mutation to programmatically mark a thread as resolved.

**Required:** The thread's GraphQL node `id` (from the `reviewThreads` query — the `id` field on each thread node, **not** the comment's `databaseId`).

**PowerShell — resolve a single thread:**

```powershell
$env:GH_HOST = 'github.docusignhq.com'  # omit for github.com

# $threadId is the node id from reviewThreads.nodes[n].id
$threadId = "MDIzOlB1bGxSZXF1ZXN0UmV2aWV3VGhyZWFkMTM..."

$resolveMutation = "mutation(`$threadId:ID!){resolveReviewThread(input:{threadId:`$threadId}){thread{isResolved}}}"

$resolveBody = @{
    query     = $resolveMutation
    variables = @{ threadId = $threadId }
} | ConvertTo-Json -Depth 3 -Compress

$resolveResult = $resolveBody | gh api graphql --input - | ConvertFrom-Json
Write-Host "isResolved: $($resolveResult.data.resolveReviewThread.thread.isResolved)"
```

### Obtaining the threadId

The `threadId` for the mutation comes from the `id` field returned by the `reviewThreads` query — **not** the comment's `databaseId` used for REST replies:

```powershell
# Fetching threads (as per earlier examples)
$thread = $threads[0]
$threadId = $thread.id        # e.g. "MDIzOlB1bGxSZXF1ZXN0UmV..."

# The comment's databaseId is used only for REST API replies:
$commentId = $thread.comments.nodes[0].databaseId   # e.g. 2145967
```

### Posting a Reply Before Resolving

To reply to a thread before resolving it, use the REST API. **Note:** The `/pulls/comments/{id}/replies` endpoint returns HTTP 404 even when the comment is accessible. Use `/pulls/{prNumber}/comments` with `in_reply_to` instead:

```powershell
# Post a reply using in_reply_to (the /replies endpoint returns 404)
gh api --method POST "repos/$owner/$repo/pulls/$prNumber/comments" `
  --field body="🤖 PR Navigator: took suggested fix" `
  --field in_reply_to=$commentId
```

Then resolve the thread using the GraphQL mutation shown above.

### Required Permissions

The authenticated user must have **write access** to the repository to resolve review threads. Read-only access will result in a `403 Forbidden` or a GraphQL authorization error.

## REST API vs GraphQL

### When to Use Each

**Use GraphQL for:**
- Fetching review threads (not available in REST)
- Complex queries with nested data
- Fetching multiple related entities
- Pagination with cursor-based navigation

**Use REST API for:**
- Simple operations
- When GraphQL schema is unclear
- Legacy scripts that use REST

### Example: GraphQL is Required for Review Threads

**REST API limitation:**
```powershell
# REST API does NOT provide reviewThreads endpoint
gh api /repos/{owner}/{repo}/pulls/{number}/reviews
# Returns review summaries, not individual threads
```

**GraphQL solution:**
```powershell
# GraphQL provides full thread access
gh api graphql -f query='...'
```

## References

**Official Documentation:**
- [GitHub CLI Manual](https://cli.github.com/manual/)
- [GitHub GraphQL API](https://docs.github.com/en/graphql)
- [PullRequest Schema](https://docs.github.com/en/graphql/reference/objects#pullrequest)

**Extended Reference:**
- [Error handling, defensive practices, filters, sorting, caching, best practices](references/REFERENCE.md)
