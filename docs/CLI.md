# CLI reference

The worker runs in two modes:

- Worker mode with no arguments: start the scheduled background pipeline
- CLI mode with arguments: execute one command and exit

Invoke commands either from the project directory or from the container:

```bash
# Local project run
dotnet run --project src/TalkingPointsSummary -- <command> [options]

# Docker Compose container
docker exec talking-points-summary dotnet TalkingPointsSummary.dll <command> [options]
```

## Command summary

| Command | Purpose |
| --- | --- |
| `add-parent` | Register a parent account and recipient emails |
| `add-child` | Add a child to an existing parent |
| `list-parents` | List parents and their children |
| `remove-parent` | Delete a parent and all associated data |
| `remove-child` | Delete a child |
| `run` | Run the full pipeline for all active parents |
| `check-config` | Validate required settings and external connectivity |

## `add-parent`

Register a parent account.

```bash
add-parent --name <name> --token <token> --contact-id <contact-id> --emails <email1;email2>
```

| Option | Required | Description |
| --- | --- | --- |
| `--name` | Yes | Parent or family label stored in the database |
| `--token` | Yes | TalkingPoints `x-token` header value |
| `--contact-id` | Yes | TalkingPoints `x-contactid` header value |
| `--emails` | Yes | Semicolon-delimited recipient list |

Example:

```bash
add-parent \
  --name "ExampleFamily" \
  --token "your-talkingpoints-x-token" \
  --contact-id "your-talkingpoints-x-contactid" \
  --emails "parent1@example.com;parent2@example.com"
```

Representative output:

```text
Added parent 'ExampleFamily' with ID 1
```

## `add-child`

Add a child to an existing parent.

```bash
add-child --parent-id <id> --name <name> --school <school> --grade <grade> [--year <year>] [--emoji <emoji>]
```

| Option | Required | Description |
| --- | --- | --- |
| `--parent-id` | Yes | Parent ID returned by `add-parent` or shown by `list-parents` |
| `--name` | Yes | Child name |
| `--school` | Yes | School name |
| `--grade` | Yes | Starting grade, where `0=Kindergarten` and `12=12th Grade` |
| `--year` | No | Starting school year, for example `2025` for the `2025-2026` school year |
| `--emoji` | No | Heading emoji stored with the child; defaults to `📚` |

If you omit `--year`, the service uses the current school year from `GradeCalculator.GetCurrentSchoolYear()`. Dates from September through December use the current calendar year. Dates from January through August use the previous calendar year.

Example:

```bash
add-child \
  --parent-id 1 \
  --name "StudentOne" \
  --school "Sample Elementary" \
  --grade 0 \
  --emoji "📚"
```

Representative output:

```text
Added child 'StudentOne' (ID 1) to parent 'ExampleFamily'
```

## `list-parents`

List all parents and their children.

```bash
list-parents
```

If there are no parents, the command prints:

```text
No parents registered.
```

Representative output:

```text

[1] ExampleFamily (active)
    Emails: parent1@example.com;parent2@example.com
    ContactId: your-talkingpoints-x-contactid
    📚 [1] StudentOne — Sample Elementary — Kindergarten
    🎓 [2] StudentTwo — Demo Elementary — 3rd Grade
```

The displayed grade is the current grade label, not the raw starting grade.

## `remove-parent`

Delete a parent and all associated data.

```bash
remove-parent --id <id>
```

| Option | Required | Description |
| --- | --- | --- |
| `--id` | Yes | Parent ID to delete |

Example:

```bash
remove-parent --id 1
```

Representative output:

```text
Removed parent 'ExampleFamily' (ID 1) and all associated data
```

This command deletes the parent, children, messages, news items, and summaries through EF Core cascade rules.

## `remove-child`

Delete a child.

```bash
remove-child --id <id>
```

| Option | Required | Description |
| --- | --- | --- |
| `--id` | Yes | Child ID to delete |

Example:

```bash
remove-child --id 2
```

Representative output:

```text
Removed child 'StudentTwo' (ID 2)
```

If the child ID is unknown, the command prints `Child with ID <id> not found` and exits with code `1`.

## `run`

Run the full pipeline for all active parents.

```bash
run
```

The CLI command does not accept `--parent-id`. Parent-scoped manual runs are available through the worker debug endpoint and the admin debug page when debug features are enabled.

What the command does:

1. Fetch messages from TalkingPoints
2. Save only new messages
3. Categorize unprocessed messages with Anthropic Haiku
4. Scrape newsletter URLs through Browserless when applicable
5. Save resulting news items
6. Generate the weekly Markdown summary with Anthropic Sonnet
7. Convert Markdown to HTML
8. Send email through SMTP
9. Archive the summary

Example:

```bash
docker exec talking-points-summary dotnet TalkingPointsSummary.dll run
```

Representative output:

```text
Starting manual pipeline run...
Pipeline run complete.
```

If another run is already in progress, the command prints `A pipeline run is already in progress.` and exits with code `1`.

## `check-config`

Validate required configuration and connectivity without running the pipeline.

```bash
check-config
```

The command runs the startup validator and reports:

1. Required config presence
2. Database connectivity and migration state
3. Anthropic API key acceptance
4. Browserless reachability
5. SMTP connectivity
6. TalkingPoints checks for each active parent

Example:

```bash
docker exec talking-points-summary dotnet TalkingPointsSummary.dll check-config
```

Representative output:

```text
Checking configuration and connectivity...

✅ PASS  Config presence           All required environment variables are set
✅ PASS  Database connection       Connected; 3 migration(s) applied, schema is up to date
✅ PASS  Anthropic API key         Key accepted by API (HTTP 400)
✅ PASS  Browserless reachability  Scrape endpoint responded successfully at http://browserless:3000/scrape
⚠️  WARN  SMTP connectivity        Connected to localhost:1025 — server does not require authentication (e.g. Mailpit)
⚠️  WARN  TalkingPoints (parents)  No active parents registered in the database

All checks passed.
```

Warnings do not fail the command. Any `FAIL` result sets exit code `1`.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Command succeeded |
| `1` | Validation failed, an entity was not found, a pipeline run was already in progress, or `check-config` reported at least one failed check |

## Finding TalkingPoints credentials

You need two headers from an authenticated TalkingPoints parent session:

- `x-token`
- `x-contactid`

To get them:

1. Open `https://families.talkingpts.org/login`.
2. Sign in to the parent account you want to summarize.
3. Open browser developer tools.
4. Open the `Network` tab and refresh the page.
5. Select any authenticated TalkingPoints API request.
6. Open `Headers` and copy `x-token` and `x-contactid` from `Request Headers`.

![TalkingPoints request headers in browser dev tools](images/talkingpoints-devtools-request-headers.png)

Keep both values out of source control.
