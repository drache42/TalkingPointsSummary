---
name: postgres-database-access
description: Connects to and queries the Aspire-managed PostgreSQL 15 database for TalkingPointsSummary. Use this when asked about database tables, running SQL queries, checking data, viewing the schema, inspecting migrations, or troubleshooting database connection issues in this project.
---

This project uses .NET Aspire to manage a local PostgreSQL 15 container for development.
The container name and host port are **dynamic** (randomly assigned by Aspire on each run).
Always resolve the live container details before connecting.

---

## Step 1 — Find the Running Container

Run the following command to identify the Postgres container name and its exposed host port:

```powershell
docker ps --format "table {{.Names}}\t{{.Image}}\t{{.Ports}}" | Select-String "postgres"
```

Expected output example:
```
postgres-ughgzjvt   postgres:15-alpine   127.0.0.1:50444->5432/tcp
```

- **Container name**: the `postgres-*` entry (e.g. `postgres-ughgzjvt`)
- **Host port**: the left side of `->5432/tcp` (e.g. `50444`)

---

## Step 2 — Connection Details

| Property   | Value                                               |
|------------|-----------------------------------------------------|
| Host       | `127.0.0.1`                                         |
| Port       | Resolved from Step 1 (dynamic)                      |
| Database   | `talkingpoints`                                     |
| Username   | `postgres`                                          |
| Password   | `postgres` (from `appsettings.json` → `Parameters.postgres-password`) |

Connection string format:
```
Host=127.0.0.1;Port=<PORT>;Database=talkingpoints;Username=postgres;Password=postgres
```

---

## Step 3 — Execute Queries via Docker

Use `docker exec` with `PGPASSWORD` to avoid interactive password prompts:

```powershell
docker exec -e PGPASSWORD=postgres <CONTAINER_NAME> psql -U postgres -d talkingpoints -c "<SQL>"
```

### Common queries

List all tables:
```powershell
docker exec -e PGPASSWORD=postgres <CONTAINER_NAME> psql -U postgres -d talkingpoints -c "\dt"
```

Count rows in a table:
```powershell
docker exec -e PGPASSWORD=postgres <CONTAINER_NAME> psql -U postgres -d talkingpoints -c "SELECT COUNT(*) FROM \"Parents\";"
```

Describe a table:
```powershell
docker exec -e PGPASSWORD=postgres <CONTAINER_NAME> psql -U postgres -d talkingpoints -c "\d \"Parents\""
```

---

## Database Schema

Managed by EF Core migrations located in `src/TalkingPointsSummary/Migrations/`.

| Table                  | Model class   | Notes                                      |
|------------------------|---------------|--------------------------------------------|
| `Parents`              | `Parent`      | Top-level entity; owns Children, Messages, NewsItems, Summaries |
| `Children`             | `Child`       | Belongs to a `Parent`                      |
| `Messages`             | `Message`     | Talking Points messages; unique on `ExternalMessageId` |
| `NewsItems`            | `NewsItem`    | Scraped/fetched news items per parent      |
| `Summaries`            | `Summary`     | AI-generated summaries per parent         |
| `__EFMigrationsHistory`| —             | EF Core migration tracking                 |

---

## Source Configuration

- Aspire AppHost: `src/TalkingPointsSummary.AppHost/Program.cs`
- Password parameter: `src/TalkingPointsSummary.AppHost/appsettings.json` → `Parameters.postgres-password`
- DbContext: `src/TalkingPointsSummary.Core/Data/AppDbContext.cs`
- Migrations: `src/TalkingPointsSummary/Migrations/`
- The `ManagePostgres` flag in `appsettings.json` controls whether Aspire spins up the container (`true`) or defers to an external connection string (`false`)

---

## Applying Migrations

Migrations run automatically on worker startup via `db.Database.MigrateAsync()` in `src/TalkingPointsSummary/Program.cs`.

To check applied migrations:
```powershell
docker exec -e PGPASSWORD=postgres <CONTAINER_NAME> psql -U postgres -d talkingpoints -c "SELECT version FROM \"__EFMigrationsHistory\" ORDER BY version;"
```
