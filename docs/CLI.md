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
| --- | --- | --- |
| `--name` | Yes | Family name (e.g. "ExampleFamily") |
| `--token` | Yes | TalkingPoints `x-token` header value |
| `--contact-id` | Yes | TalkingPoints `x-contactid` header value |
| `--emails` | Yes | Semicolon-delimited recipient email addresses |

**Example:**

```bash
add-parent \
  --name "ExampleFamily" \
  --token "your-talkingpoints-x-token" \
  --contact-id "your-talkingpoints-x-contactid" \
  --emails "parent1@gmail.com;parent2@gmail.com"
```

**Output:**

```text
Added parent 'ExampleFamily' with ID 1
```

---

### `add-child`

Add a child to an existing parent.

```bash
add-child --parent-id <id> --name <name> --school <school> --grade <grade> [--year <year>] [--emoji <emoji>]
```

| Option | Required | Description |
| --- | --- | --- |
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
  --name "StudentOne" \
  --school "Sample Elementary" \
  --grade 0 \
  --emoji "📚"
```

**Output:**

```text
Added child 'StudentOne' (ID 1) to parent 'ExampleFamily'
```

**Grade reference:**

| Value | Grade |
| --- | --- |
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

```text
[1] ExampleFamily (active)
    Emails: parent1@gmail.com;parent2@gmail.com
    ContactId: your-talkingpoints-x-contactid
  📚 [1] StudentOne — Sample Elementary — Kindergarten
  🎓 [2] StudentTwo — Demo Elementary — 3rd Grade
```

---

### `remove-parent`

Remove a parent and all associated data (children, messages, news, summaries).

```bash
remove-parent --id <id>
```

| Option | Required | Description |
| --- | --- | --- |
| `--id` | Yes | Parent ID to remove |

**Example:**

```bash
remove-parent --id 1
```

**Output:**

```text
Removed parent 'ExampleFamily' (ID 1) and all associated data
```

> ⚠️ This is destructive — all messages, news items, and summaries for this parent will be deleted (cascade).

---

### `remove-child`

Remove a single child from a parent.

```bash
remove-child --id <id>
```

| Option | Required | Description |
| --- | --- | --- |
| `--id` | Yes | Child ID to remove |

**Example:**

```bash
remove-child --id 2
```

**Output:**

```text
Removed child 'StudentTwo' (ID 2)
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

```text
Starting manual pipeline run...
Pipeline run complete.
```

---

### `check-config`

Verify required configuration and external service connectivity without starting a pipeline run.

```bash
check-config
```

**What it does:**

1. Validates required configuration values are present
2. Checks external service connectivity through `StartupValidator`
3. Reports pass, warning, and failure results in the console
4. Returns a non-zero exit code if any required check fails

**Example:**

```bash
docker exec talking-points-summary dotnet TalkingPointsSummary.dll check-config
```

**Output:**

```text
Checking configuration and connectivity...

✅ PASS  Database            Connected successfully
✅ PASS  Browserless         Reachable
⚠️  WARN  SMTP              Using local development SMTP settings
```

---

## Exit Codes

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Error (e.g. parent/child not found) |

## Finding TalkingPoints Credentials

This is usually the hardest part of setup. You need two values from your own authenticated TalkingPoints browser session:

- `x-token`
- `x-contactid`

Walkthrough:

1. Open [https://families.talkingpts.org/login](https://families.talkingpts.org/login).
2. Enter the phone number for the parent account you want to summarize.
3. Complete sign-in with the verification code TalkingPoints sends you.
4. Once you are signed in, open browser developer tools. `F12` is the usual shortcut.
5. In developer tools, open the `Network` tab.
6. Refresh the page after the Network tab is already open.
7. Click any authenticated TalkingPoints request in the left-hand request list.
8. In `Headers`, look under `Request Headers`.
9. Copy the values for `x-token` and `x-contactid`.

![TalkingPoints request headers in browser dev tools](images/talkingpoints-devtools-request-headers.png)

Notes:

- The exact request name is not important as long as it is a logged-in TalkingPoints API request.
- If the list is empty or missing the headers, refresh again after sign-in completes.
- Treat these values like credentials and keep them out of source control.
