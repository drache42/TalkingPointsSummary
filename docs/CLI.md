# CLI Reference

The Talking Points Summary app runs in two modes:

1. **Worker mode** (default): Runs as a long-lived service, triggering the pipeline on schedule
2. **CLI mode**: Executes a command and exits

CLI commands are invoked by passing arguments to the application entry point:

```bash
# Locally
dotnet run -- <command> [options]

# In Docker
docker exec talking-points-summary dotnet TalkingPointsSummary.dll <command> [options]
```

---

## Commands

### `add-parent`

Register a new parent account.

```bash
add-parent --name <name> --token <token> --contact-id <id> --emails <emails>
```

| Option | Required | Description |
|---|---|---|
| `--name` | Yes | Family name (e.g. "Froehlich") |
| `--token` | Yes | TalkingPoints `x-token` header value |
| `--contact-id` | Yes | TalkingPoints `x-contactid` header value |
| `--emails` | Yes | Semicolon-delimited recipient email addresses |

**Example:**

```bash
add-parent \
  --name "Froehlich" \
  --token "your-talkingpoints-x-token" \
  --contact-id "your-talkingpoints-x-contactid" \
  --emails "parent1@gmail.com;parent2@gmail.com"
```

**Output:**

```
Added parent 'Froehlich' with ID 1
```

---

### `add-child`

Add a child to an existing parent.

```bash
add-child --parent-id <id> --name <name> --school <school> --grade <grade> [--year <year>] [--emoji <emoji>]
```

| Option | Required | Description |
|---|---|---|
| `--parent-id` | Yes | Parent ID (from `add-parent` output or `list-parents`) |
| `--name` | Yes | Child's name |
| `--school` | Yes | School name |
| `--grade` | Yes | Starting grade level (0 = Kindergarten, 1 = 1st, etc.) |
| `--year` | No | School year when the grade applies (e.g. 2025 = 2025-2026 school year). Defaults to the current school year if omitted. |
| `--emoji` | No | Emoji for summary headings (default: "📚") |

**Example:**

```bash
add-child \
  --parent-id 1 \
  --name "Clara" \
  --school "James Baldwin Elementary" \
  --grade 0 \
  --emoji "📚"
```

**Output:**

```
Added child 'Clara' (ID 1) to parent 'Froehlich'
```

**Grade reference:**

| Value | Grade |
|---|---|
| 0 | Kindergarten |
| 1 | 1st Grade |
| 2 | 2nd Grade |
| 3 | 3rd Grade |
| 4+ | 4th Grade, etc. |

Grades auto-advance every September 1st based on the starting grade and year.

---

### `list-parents`

Display all registered parents and their children.

```bash
list-parents
```

**Example output:**

```
[1] Froehlich (active)
    Emails: parent1@gmail.com;parent2@gmail.com
    ContactId: your-talkingpoints-x-contactid
    📚 [1] Clara — James Baldwin Elementary — Kindergarten
    🎓 [2] Nolan — Cascadia Elementary — 3rd Grade
```

---

### `remove-parent`

Remove a parent and all associated data (children, messages, news, summaries).

```bash
remove-parent --id <id>
```

| Option | Required | Description |
|---|---|---|
| `--id` | Yes | Parent ID to remove |

**Example:**

```bash
remove-parent --id 1
```

**Output:**

```
Removed parent 'Froehlich' (ID 1) and all associated data
```

> ⚠️ This is destructive — all messages, news items, and summaries for this parent will be deleted (cascade).

---

### `remove-child`

Remove a single child from a parent.

```bash
remove-child --id <id>
```

| Option | Required | Description |
|---|---|---|
| `--id` | Yes | Child ID to remove |

**Example:**

```bash
remove-child --id 2
```

**Output:**

```
Removed child 'Nolan' (ID 2)
```

---

### `run`

Manually trigger the full pipeline for all active parents. Useful for testing and initial setup.

```bash
run
```

**What it does:**

1. Fetches messages from TalkingPoints API
2. Deduplicates and stores new messages
3. AI-categorizes unprocessed messages (Claude Haiku)
4. Scrapes newsletter URLs (Browserless)
5. Stores categorized news items
6. Generates weekly summary (Claude Sonnet)
7. Converts Markdown → HTML
8. Sends email to all recipients
9. Archives the summary

**Example:**

```bash
docker exec talking-points-summary dotnet TalkingPointsSummary.dll run
```

**Output:**

```
Starting manual pipeline run...
Pipeline run complete.
```

---

## Exit Codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Error (e.g. parent/child not found) |

## Finding TalkingPoints Credentials

To get the `--token` and `--contact-id` values:

1. Log in to [TalkingPoints](https://app.talkingpts.org) in your browser
2. Open browser Developer Tools (F12) → Network tab
3. Navigate to Messages
4. Find a request to `/api/parents/v3/messages/feed`
5. Copy the `x-token` and `x-contactid` header values
