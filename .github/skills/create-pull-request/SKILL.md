---
name: create-pull-request
description: Drafts and opens a GitHub pull request for the current branch in TalkingPointsSummary. Use this when asked to open, create, or draft a PR. Analyzes the diff against origin/main, generates a suggested title, writes a 1-3 line summary and a feature-breakdown body, shows the full draft to the user for approval, and only posts it after receiving explicit confirmation.
---

# Skill: Create Pull Request

**Repo**: `drache42` / `TalkingPointsSummary`

---

## CRITICAL: Never Post Without Explicit Approval

Draft the PR content and present it to the user. **Do NOT call any tool that opens or submits the PR** until the user replies with explicit approval (e.g. "looks good", "post it", "yes", "submit it").

---

## Step 1 — Gather Branch & Diff Information

Run these commands in sequence. Use the workspace folder `d:\Code\TalkingPointsSummary`.

```powershell
# 1a. Current branch name
git -C "d:\Code\TalkingPointsSummary" rev-parse --abbrev-ref HEAD

# 1b. Commit log since divergence from base branch
git -C "d:\Code\TalkingPointsSummary" log origin/main..HEAD --oneline

# 1c. Full diff (stat summary for file-level overview)
git -C "d:\Code\TalkingPointsSummary" diff origin/main...HEAD --stat

# 1d. Full diff content (for understanding what changed)
git -C "d:\Code\TalkingPointsSummary" diff origin/main...HEAD
```

If the user specifies a different base branch, replace `origin/main` with it in all commands.

> If the diff is very large (> 400 lines), read the `--stat` output and the commit log first, then read the diff in targeted sections using `git diff origin/main...HEAD -- <path>` for the most significant files.

---

## Step 2 — Analyze the Changes

From the diff and commit log, identify the logical **features or concerns** changed. Examples of groupings:
- A new service or class added
- A configuration change
- A bug fix
- A refactor of an existing component
- Tests added or updated
- Infrastructure / build changes
- Documentation updates

Do NOT list every file or line. Group changes by what they accomplish.

---

## Step 2b — Classify the Semver Bump

Load and follow the **`semver-classification`** skill (`.github/skills/semver-classification/SKILL.md`). The diff and commit log gathered in Steps 1 and 2 are already available — pass them directly to the skill's Step 2 classification rules; there is no need to re-run the git commands.

Record the result: **`patch`**, **`minor`**, or **`major`**, plus the one-sentence rationale produced by the skill.

---

## Step 3 — Draft the PR

Compose the following:

### Suggested Title
One concise line describing the overall change. Use imperative mood (e.g. "Add weekly digest scheduling" not "Added...").

### PR Body

```markdown
## Summary

<1–3 sentences describing the overall purpose of this PR at a high level.>

## Changes

### <Feature/Concern 1 Name>
<2–4 sentence explanation of what was added or changed and why.>

### <Feature/Concern 2 Name>
<2–4 sentence explanation.>

<!-- repeat for each logical group — typically 2–5 sections -->

## Version Hint

**Suggested bump:** `patch` | `minor` | `major`
> <One sentence explaining the primary signal that drove this classification, e.g. "New `DigestSchedulerService` added with no breaking changes to existing configuration or schema.">
```

---

## Step 4 — Present Draft to the User

Show the user the full draft in this format:

```
**Proposed PR**

**Title:** <suggested title>

**Base branch:** main (or the user-specified branch)
**Head branch:** <current branch>
**Draft PR:** Yes / No

---
<full PR body>
---

Does this look good? Reply "post it" (or "post as draft" if you want a draft PR) to submit, or tell me what to change.
```

**STOP HERE and wait for user response.**

---

## Step 5 — Check Tooling Availability

Once the user approves, determine how to submit the PR:

**Option A: GitHub MCP tools (preferred)**
Check whether `mcp_github_create_pull_request` is available by recalling tools from the current session. If it was returned earlier by `tool_search_tool_regex`, it is available.

**Option B: GitHub CLI (`gh`)**
If MCP tools are not available, check for the `gh` CLI:
```powershell
gh --version
```
If this succeeds, use `gh pr create` (see Step 6B).

**Option C: Manual**
If neither is available, display the PR title and body and instruct the user to open the PR manually at:
`https://github.com/drache42/TalkingPointsSummary/compare/<head-branch>?expand=1`

---

## Step 6A — Submit via GitHub MCP Tool

Use `mcp_github_create_pull_request` with:
- `owner`: `drache42`
- `repo`: `TalkingPointsSummary`
- `title`: the approved title
- `body`: the approved PR body
- `head`: the current branch name (from Step 1a)
- `base`: `main` (or user-specified base)
- `draft`: `true` only if the user explicitly requested a draft PR

---

## Step 6B — Submit via `gh` CLI

```powershell
gh pr create `
  --repo drache42/TalkingPointsSummary `
  --title "<approved title>" `
  --body "<approved body>" `
  --base main `
  --head <current-branch>
  # append --draft only if user requested it
```

---

## Step 7 — Confirm

After the PR is created (via MCP or CLI), report the PR URL back to the user.

If submitted manually (Option C), remind the user to copy the title and body above into the GitHub UI.
