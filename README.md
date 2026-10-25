# SivaComm

One messaging service for every Sivayaan application: WhatsApp OTPs for
password reset, welcome messages, delivery tracking, and a small admin
dashboard. Applications call it server to server; browsers never do.

**The rule it is built around:** the application that owns the user owns the
password reset. SivaComm generates, sends and verifies the code and hands the
application's backend a one-time grant; the application checks that grant,
changes the password in its own database, and nothing about the password
ever reaches SivaComm (its request bodies reject unknown fields, so one
cannot even be sent by mistake).

## Layout

```
backend/
  SivaComm.slnx
  src/SivaComm.Data     entities, EF Core + PostgreSQL (schema "comm"), OTP/grant/outbox/webhook services, providers
  src/SivaComm.Api      ASP.NET Core API, signature auth, admin cookie auth, hosted outbox worker, serves the dashboard
  tests/SivaComm.Tests  real-PostgreSQL tests: OTP rules, concurrency, grants, outbox, webhooks, HTTP auth
dashboard/              React + TypeScript (Vite) admin UI; `npm run build` writes into the API's wwwroot
docs/
```

## Where to look

| I want to… | Read |
|---|---|
| **Read everything — flows, states, failures, all configuration** | [docs/GUIDE.md](docs/GUIDE.md) |
| See every request and response, hop by hop, as diagrams | [docs/WORKFLOWS.md](docs/WORKFLOWS.md) |
| Print or share the flow diagrams (PDF, 6 pages) | [docs/diagrams/SivaComm-Flow-Diagrams.pdf](docs/diagrams/SivaComm-Flow-Diagrams.pdf) — source: [flow-diagrams.html](docs/diagrams/flow-diagrams.html) |
| Create the database by SQL (role, database, schema; or everything incl. tables) | `deploy/sql/01-create-database.sql`, generated `db/full-schema.sql` and `db/migrate.sql` (regenerate: `deploy/New-SchemaScripts.ps1`) |
| **Deploy to production: API on the Azure VM, dashboard on Cloudflare Pages** | [docs/DEPLOYMENT_AZURE.md](docs/DEPLOYMENT_AZURE.md) — zips from `deploy/New-Release.ps1`, installed with `Install-SivaComm.ps1` |
| Configure and deploy, step by step (all settings in config files) | [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) — `deploy/Deploy-SivaComm.ps1`, `deploy/appsettings.Production.json.template` |
| Set up the messages (Meta templates, server config, dashboard, app config) | [docs/CONFIGURATION.md](docs/CONFIGURATION.md) |
| Call SivaComm from an application | [docs/API.md](docs/API.md) — endpoints, signature, statuses |
| Understand the OTP and grant rules | [docs/SECURITY.md](docs/SECURITY.md) |
| Connect a real WhatsApp number | [docs/WHATSAPP_SETUP.md](docs/WHATSAPP_SETUP.md) |
| See the design and what is still open | [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) |

## Status

- **HMS_WEB** is integrated, behind its `SivaComm:Enabled` setting
  (default stays `false`, the normal flow). See HMS_WEB `docs/PASSWORD_RESET.md`.
- **Swarnakshi, TransTrack Web**: next, after HMS_WEB is proven. Neither has
  a self-service reset today, so each needs its own reset flow built, not
  just a re-wire.
- **WhatsApp**: implemented against Meta's Cloud API, **not yet exercised
  against a live account** — no credentials are configured. Until they are,
  messages go to the log (codes visible only in Development).
- **SMS**: not implemented. It is another `IMessageProvider`.

## Quick start (development)

```powershell
cd backend
dotnet run --project src/SivaComm.Api          # http://localhost:5290, migrates sivacomm_dev on start
cd ../dashboard; npm install; npm run dev       # http://localhost:5291, proxies /api to the API
```

Needs `backend/src/SivaComm.Api/appsettings.Local.json` with a database
password and a bootstrap admin — see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

```powershell
$env:SIVACOMM_TEST_PG = "Host=localhost;Username=sivacomm;Password=..."   # a role with CREATEDB
dotnet test backend
```
