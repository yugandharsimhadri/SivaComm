# Running and deploying SivaComm

One process: the API, the outbox worker (a hosted service inside it) and the
dashboard (static files it serves). One PostgreSQL database of its own,
schema `comm`. No Redis, no queue broker — the outbox is a table claimed with
`FOR UPDATE SKIP LOCKED`, which is enough at this volume and keeps the
moving parts to the two that already exist.

## 1 · Database

As the PostgreSQL superuser, once:

```sql
CREATE ROLE sivacomm LOGIN PASSWORD '…';
CREATE DATABASE sivacomm OWNER sivacomm ENCODING 'UTF8' TEMPLATE template0;
```

For a **developer machine**, the role also needs `CREATEDB` (the tests make a
throwaway database per test, and a Development run creates `sivacomm_dev`):

```sql
CREATE ROLE sivacomm LOGIN CREATEDB PASSWORD 'sivacomm-dev';
```

`sivacomm-dev` is the password the tests assume when `SIVACOMM_TEST_PG` is
not set. Nothing here touches any other application's database.

## 2 · Configuration

`appsettings.json` is committed and holds no secrets. Put them in
`backend/src/SivaComm.Api/appsettings.Local.json` (development) or
`appsettings.Production.json` (server) — both git-ignored — or environment
variables (`Database__Password`, `Security__MasterKey`, …).

```json
{
  "Database": { "Password": "…" },
  "Security": { "MasterKey": "<32 random bytes, base64>" },
  "Admin":    { "Username": "ops", "Password": "<first dashboard login>" },
  "WhatsApp": { "AccessToken": "…", "PhoneNumberId": "…", "AppSecret": "…", "WebhookVerifyToken": "…" }
}
```

Generate a master key:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Development uses a fixed, public key from `appsettings.Development.json`; the
API refuses that key, or no key, anywhere else.

`Admin` only creates the first operator while the admin table is empty; once
one exists, editing it does nothing.

## 3 · Release

```powershell
cd backend
dotnet publish src/SivaComm.Api -c Release -o ..\publish      # after: cd dashboard; npm run build
..\publish\SivaComm.Api.exe --migrate                          # apply migrations, exit
..\publish\SivaComm.Api.exe                                    # serve
```

Migrations run automatically only in Development (`Database:MigrateOnStartup`
overrides). Build the dashboard before publishing: `npm run build` in
`dashboard/` writes to the API's `wwwroot`, which publish then includes.

Behind a reverse proxy or tunnel on the same machine, `X-Forwarded-Proto/For`
from loopback are honoured. **HTTPS is required** for anything but localhost:
the admin cookie is `Secure` outside Development.

## 4 · Registering an application

In the dashboard → Applications → Register (code e.g. `hms-web`). Copy the
key id and secret shown once into that application's server config. Then
set its two templates:

| Purpose | Template | Parameters |
|---|---|---|
| Password-reset OTP | the approved **Authentication** template name | 1 (the code; fixed) |
| Welcome | e.g. `hms_welcome` | as approved (HMS: 2 — clinic, username) |

Or from the command line, before the dashboard has an operator:
`SivaComm.Api.exe --register-app hms-web "Sivayaan HMS"` (prints the key once;
templates are then set in the dashboard).

**Rotation:** Issue a second key → deploy it to the application → revoke the
old one. **Compromise:** revoke immediately; the application fails closed
(every reset answers `TEMPORARY_ERROR`) until it has a new key.

## 5 · Health

`/health/live` for "process up", `/health/ready` for "database reachable and
worker looping". The dashboard's Overview shows worker heartbeat, last
provider acceptance and last failure.
