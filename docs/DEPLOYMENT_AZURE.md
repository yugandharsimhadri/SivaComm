# Deploying SivaComm: API on the Azure VM, dashboard on Cloudflare

Step by step, from two zip files to a working service. Every setting is in a
config file; nothing is typed into the application.

| | Address | Runs on |
|---|---|---|
| **Dashboard** | `https://sivacom.sivayaantechnologies.com` | Cloudflare Pages (static files) |
| **API, public** | `https://sivacomapi.sivayaantechnologies.com` | Cloudflare Tunnel → the VM |
| **API, on the VM** | `http://localhost:6071` | Windows service `SivaComApi`, folder `C:\Server\sivacomapi` |
| **Database** | `localhost:5432`, database `sivacomm`, role `sivacomm` | PostgreSQL on the VM |

```
 Operator browser ──https──> sivacom.sivayaantechnologies.com        (Cloudflare Pages: dashboard files)
        │
        └──── API calls, with the login cookie ────┐
                                                  v
 Meta webhook ──https──> sivacomapi.sivayaantechnologies.com  (Cloudflare edge, TLS)
                                                  │  tunnel — outbound from the VM, no open ports
                                                  v
 Azure VM:   cloudflared ──> http://localhost:6071  SivaComApi service ──> PostgreSQL localhost:5432
                                   ^
 HMS_WEB API ──signed calls────────┘   (http://localhost:6071 if HMS is on this VM,
                                        https://sivacomapi.… if it is elsewhere)
```

- [1. Before you start](#1-before-you-start)
- [2. Build the two zips](#2-build-the-two-zips-development-machine)
- [3. Prepare the VM](#3-prepare-the-vm-once)
- [4. Copy the API zip and create the database](#4-copy-the-api-zip-and-create-the-database-once)
- [5. Fill in the config](#5-fill-in-the-config)
- [6. Install the API](#6-install-the-api)
- [7. Publish the API through the tunnel](#7-publish-the-api-through-the-tunnel)
- [8. Dashboard on Cloudflare Pages](#8-dashboard-on-cloudflare-pages)
- [9. Meta webhook](#9-meta-webhook)
- [10. Point HMS_WEB at SivaComm](#10-point-hms_web-at-sivacomm)
- [11. Check it end to end](#11-check-it-end-to-end)
- [12. Releasing a new version](#12-releasing-a-new-version)
- [13. When something is wrong](#13-when-something-is-wrong)
- [Appendix: every value and where it goes](#appendix-every-value-and-where-it-goes)

---

## 1. Before you start

| Need | Notes |
|---|---|
| The Azure VM (Windows Server), RDP as an administrator | the same VM as HMS_WEB is fine |
| Cloudflare account with the `sivayaantechnologies.com` zone | where `hoapi`/`healthone` already live |
| A development machine with .NET 10 SDK, Node 20+, git and the SivaComm repo | builds the zips |
| Meta values (WHATSAPP_SETUP.md): phone number id, permanent token, app secret, approved template names | optional to start — without them SivaComm runs but sends nothing |

Nothing in this guide opens an Azure inbound port: the tunnel connects
outward from the VM, exactly as for HMS_WEB.

---

## 2. Build the two zips (development machine)

```powershell
cd C:\src\SivaComm
git pull
.\deploy\New-Release.ps1
```

It ends with:

```
==> API zip       : C:\src\SivaComm\artifacts\SivaComm-api-20261025-878a350.zip (… MB)
==> Dashboard zip : C:\src\SivaComm\artifacts\SivaComm-dashboard-20261025-878a350.zip (… KB)
==> Dashboard calls: https://sivacomapi.sivayaantechnologies.com
```

| Zip | Goes to | Contains |
|---|---|---|
| `SivaComm-api-….zip` | the Azure VM | `api\` (the program), `Install-SivaComm.ps1`, `sql\` (database scripts), `appsettings.Production.json.template`, `cloudflared\config.yml.template`, this guide, `VERSION.txt` |
| `SivaComm-dashboard-….zip` | Cloudflare Pages | `index.html`, `assets\`, `_headers` |

The dashboard's API address is baked in at build time from
`dashboard\.env.production` (`VITE_API_URL=https://sivacomapi.sivayaantechnologies.com`).
The script refuses to finish if that address is missing from the bundle. A
name ending in `-dirty` means it was built from uncommitted changes.

---

## 3. Prepare the VM (once)

RDP in, open **PowerShell as Administrator**.

**ASP.NET Core 10 runtime** (not the SDK; SivaComm runs as a Windows service,
so the IIS Hosting Bundle is not required — though it includes this runtime
if HMS already installed it):

```powershell
dotnet --list-runtimes | Select-String 'AspNetCore.App 10'
# nothing printed? install it:
winget install --id Microsoft.DotNet.AspNetCore.10 --accept-source-agreements --accept-package-agreements
```

**PostgreSQL** — already installed if HMS_WEB runs here:

```powershell
Get-Service postgresql*
```

**cloudflared** — already installed if HMS_WEB's tunnel runs here:

```powershell
Get-Service cloudflared
# not installed?
winget install --id Cloudflare.cloudflared
```

**Folders:**

```powershell
New-Item -ItemType Directory -Force C:\Server\sivacomapi, C:\Server\sivacomapi\logs, C:\Server\sivacomapi\keys, C:\Server\sivacomapi-backups | Out-Null
```

| Folder | Holds | Replaced on upgrade? |
|---|---|---|
| `C:\Server\sivacomapi` | the program + `appsettings.Production.json` | program yes, config **never** |
| `C:\Deployments\sivacom\release-<date>` | each unzipped release | a new one per release |
| `C:\Server\sivacomapi\logs` | daily log files | no |
| `C:\Server\sivacomapi\keys` | dashboard login-cookie keys | no |
| `C:\Server\sivacomapi-backups` | database dumps | no |

---

## 4. Copy the API zip and create the database (once)

**Copy** `SivaComm-api-….zip` to the VM (RDP clipboard / drive redirection,
or a storage blob) and unzip it:

```powershell
$rel = "C:\Deployments\sivacom\release-$(Get-Date -Format yyyyMMdd)"
Expand-Archive C:\Users\<you>\Downloads\SivaComm-api-*.zip -DestinationPath $rel -Force
cd $rel
Get-Content .\VERSION.txt
```

The zip's `sql\` folder holds three scripts, generated from this release's
own migrations:

| Script | Does | Run as |
|---|---|---|
| `sql\01-create-database.sql` | role `sivacomm` (with its password), database `sivacomm` owned by it, schema `comm`, privileges; stops other roles connecting | `postgres` |
| `sql\full-schema.sql` | all of `01-create-database.sql`, then every table, index and constraint — a ready database in one run | `postgres` |
| `sql\migrate.sql` | tables only, for an existing database; idempotent | `sivacomm` |

All three are safe to run again. They agree with the installer's own
migration step: whatever one of them has applied, the others skip.

Choose a strong password for the application role — call it **D1** — then
pick **one** of the two ways below. (`-U postgres` asks for the PostgreSQL
superuser password chosen when PostgreSQL was installed. Adjust `18` to the
installed version.)

```powershell
$psql = "C:\Program Files\PostgreSQL\18\bin\psql.exe"
```

**A — database only; the installer creates the tables (recommended):**

```powershell
& $psql -U postgres -v app_password='D1' -f .\sql\01-create-database.sql
```

The database is empty afterwards; step 6's installer creates the tables
(`SivaComm.Api.exe --migrate`).

**B — database and tables now, for a DBA who wants to see the SQL run:**

```powershell
& $psql -U postgres -v app_password='D1' -f .\sql\full-schema.sql
```

It ends by listing the applied migration and the table count (10 tables in
schema `comm`, owned by `sivacomm`). The installer later finds them in place
and only reports *"Already current."*

Either way, without `-v app_password=…` the script stops with
*"app_password is required"* and changes nothing. The role never gets
`CREATEDB`. Optional variables: `-v app_user=…` and `-v db_name=…` (defaults
`sivacomm`/`sivacomm`).

**Azure Database for PostgreSQL** instead of PostgreSQL on the VM? Run the
same script against it as its admin user (`-h <server>.postgres.database.azure.com
-U <admin> -d postgres`), and in step 5 set `Database:Host` to that server
name and `Database:SslMode` to `Require`.

**Later releases** that add migrations need nothing here: the installer
applies them. A DBA who prefers to apply them by hand runs the new release's
`sql\migrate.sql` as `sivacomm` before the installer.

---

## 5. Fill in the config

In the same folder:

```powershell
.\Install-SivaComm.ps1
```

On the first run the installer copies the template to
`C:\Server\sivacomapi\appsettings.Production.json` and stops:

```
!!  No configuration yet. A template was copied to:
!!      C:\Server\sivacomapi\appsettings.Production.json
!!  Fill in every REPLACE-ME …, then run this script again.
```

**Generate the secrets** (same PowerShell window):

```powershell
function New-Secret { $b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b) }
"S1 master key      : $(New-Secret)"
"S2 admin password  : $(New-Secret)"
"S3 webhook token   : $(New-Secret)"
"S4 HMS app secret  : $(New-Secret)"
```

Store all four in your password manager now. **S1 must also be kept off this
VM**, separately from database backups — without it stored secrets cannot be
read.

**Fill in the file:**

```powershell
notepad C:\Server\sivacomapi\appsettings.Production.json
```

Change these values (everything else can stay as the template has it):

| Key | Value |
|---|---|
| `Urls` | `http://localhost:6071` *(already set)* |
| `Cors:AllowedOrigins` | `[ "https://sivacom.sivayaantechnologies.com" ]` *(already set — exactly this, no trailing slash)* |
| `Database:Password` | **D1** |
| `Security:MasterKey` | **S1** |
| `Admin:Username` / `Admin:Password` | `ops` / **S2** — the first dashboard login |
| `WhatsApp:AccessToken` | Meta permanent token — or leave `""` for now |
| `WhatsApp:PhoneNumberId` | Meta phone number id — or `""` |
| `WhatsApp:AppSecret` | Meta app secret — or `""` |
| `WhatsApp:WebhookVerifyToken` | **S3** |
| `Applications[0].Keys[0].KeyId` | e.g. `hms-2026-10` — call it **K1** |
| `Applications[0].Keys[0].Secret` | **S4** |
| `Applications[0].Templates.PasswordResetOtp.Name` / `Language` | approved Authentication template, e.g. `sivayaan_otp` / `en` |
| `Applications[0].Templates.Welcome.Name` / `Language` / `Parameters` | approved Utility template, e.g. `hms_welcome` / `en` / `2` |
| `DataProtection:KeysPath` | `C:\\Server\\sivacomapi\\keys` *(already set)* |
| `Logging:File:PathFormat` | `C:\\Server\\sivacomapi\\logs\\sivacomm-{Date}.log` *(already set)* |

In JSON, every backslash in a path is written twice (`C:\\Server\\sivacomapi\\keys`).

**Lock the file** to administrators and the service account:

```powershell
icacls C:\Server\sivacomapi\appsettings.Production.json /inheritance:r /grant:r "Administrators:F" "SYSTEM:R"
```

---

## 6. Install the API

Same folder, same window:

```powershell
.\Install-SivaComm.ps1
```

```
==> Stopping SivaComApi                      (not on the first install)
==> Copying the build to C:\Server\sivacomapi (keeping appsettings.Production.json)
==> Applying database migrations
Applying 1: 20261024155431_Initial            ("Already current." if step 4 used full-schema.sql)
==> Creating service SivaComApi
==> Starting SivaComApi
==> Ready: http://localhost:6071/health/ready
```

On a brand-new database the migration step also prints two red
`fail: … SELECT migration_id, product_version FROM "__EFMigrationsHistory"`
blocks. That is the migrator checking for its history table before creating
it — expected on the first install, and harmless; the line that matters is
`Applying 1: …` followed by `==> Ready`.

What it refuses, with the reason: a missing ASP.NET Core 10 runtime, invalid
JSON, any `REPLACE-ME` left, a database it cannot reach, and — from the
application itself — a missing or development master key, or an invalid
`Applications` entry (every problem listed at once).

**Check on the VM, before any tunnel:**

```powershell
Invoke-WebRequest http://localhost:6071/health/ready -UseBasicParsing | Select StatusCode, Content
Get-Content (Get-ChildItem C:\Server\sivacomapi\logs\*.log | Sort LastWriteTime | Select -Last 1).FullName -Tail 20
```

The log should contain:

```
[INF] Application from configuration — "hms-web: enabled, 1 live key(s), OTP template sivayaan_otp, welcome template hms_welcome"
[WRN] Created the first dashboard admin 'ops' from configuration.
```

(and, while the `WhatsApp` values are empty, *"No WhatsApp provider is
configured. Messages are NOT sent…"* — expected).

---

## 7. Publish the API through the tunnel

**Which case are you in?** Run on the VM:

```powershell
Get-Service cloudflared
Test-Path C:\Windows\System32\config\systemprofile\.cloudflared\config.yml
```

- `cloudflared` **not found** → no tunnel on this VM: use *Dashboard-managed tunnel* below.
- found, and `True` → a config-file tunnel runs here: *If HMS_WEB's tunnel already runs on this VM*.
- found, and `False` → a dashboard-managed tunnel runs here: *Dashboard-managed tunnel*, step 3 onwards.

**The one rule that matters:** the `sivacomapi` hostname must be served by a
tunnel whose connector runs **on the same VM as SivaComApi**, with service
**HTTP** `127.0.0.1:6071`, and must not also be listed on any other tunnel.

### Dashboard-managed tunnel (recommended)

1. If `cloudflared` is not installed: `winget install --id Cloudflare.cloudflared`.
2. Cloudflare **Zero Trust → Networks → Tunnels → Create a tunnel →
   Cloudflared**, name `sivacom-api`, environment **Windows**. Run the
   `cloudflared.exe service install <token>` command it shows, on this VM.
   The tunnel turns **Healthy**.
3. That tunnel → **Public hostname → Add a public hostname**:

   | Field | Value |
   |---|---|
   | Subdomain | `sivacomapi` |
   | Domain | `sivayaantechnologies.com` |
   | Type | **HTTP** (not HTTPS) |
   | URL | `127.0.0.1:6071` |

4. Open **every other tunnel** (including HMS's `hoapi` one): none may list
   `sivacomapi`. Delete it wherever else it appears.

### If HMS_WEB's tunnel already runs on this VM, from a config file

1. Open its config:
   ```powershell
   notepad C:\Windows\System32\config\systemprofile\.cloudflared\config.yml
   ```
2. Add this rule to `ingress:`, **above** the last `- service: http_status:404` line:
   ```yaml
     - hostname: sivacomapi.sivayaantechnologies.com
       service: http://127.0.0.1:6071
       originRequest:
         connectTimeout: 30s
         httpHostHeader: sivacomapi.sivayaantechnologies.com
   ```
3. Create the DNS record (use the tunnel name from the file, e.g. `sivayaan-hms-api`):
   ```powershell
   cloudflared tunnel route dns sivayaan-hms-api sivacomapi.sivayaantechnologies.com
   ```
   (Run where `cloudflared tunnel login` was done; or in the Cloudflare
   dashboard: Zero Trust → Networks → Tunnels → the tunnel → Public hostnames
   → Add: subdomain `sivacomapi`, service `http://localhost:6071`.)
4. Restart:
   ```powershell
   Restart-Service cloudflared
   ```

### If there is no tunnel yet

```powershell
cloudflared tunnel login                          # browser opens; choose sivayaantechnologies.com
cloudflared tunnel create sivacomm-api            # prints the tunnel id, writes <id>.json
Copy-Item "$env:USERPROFILE\.cloudflared\<id>.json" C:\Windows\System32\config\systemprofile\.cloudflared\
Copy-Item .\cloudflared\config.yml.template C:\Windows\System32\config\systemprofile\.cloudflared\config.yml
notepad C:\Windows\System32\config\systemprofile\.cloudflared\config.yml   # put <id> in both places
cloudflared tunnel route dns sivacomm-api sivacomapi.sivayaantechnologies.com
cloudflared service install
Start-Service cloudflared
```

### Check from anywhere

```powershell
Invoke-WebRequest https://sivacomapi.sivayaantechnologies.com/health/ready -UseBasicParsing | Select StatusCode, Content
```

Windows PowerShell 5.1 needs TLS 1.2 switched on first, or every HTTPS call
fails with *"The underlying connection was closed"*:

```powershell
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
```

`200 ready` is success. `530`/`1033`: the tunnel is not connected. `502`:
the tunnel is up but its connector gets no answer — the public hostname's
type is HTTPS instead of HTTP, its URL is not `127.0.0.1:6071`, the connector
runs on a different machine from SivaComApi, or `sivacomapi` is still listed
on another tunnel.

---

## 8. Dashboard on Cloudflare Pages

### First time

1. Cloudflare dashboard → **Workers & Pages** → **Create** → **Pages** →
   **Upload assets** (Direct Upload).
2. Project name: `sivacom-dashboard` → **Create project**.
3. Drag in the **dashboard zip** (or its unzipped folder) → **Deploy site**.
4. The project → **Custom domains** → **Set up a custom domain** →
   `sivacom.sivayaantechnologies.com` → **Activate domain** (the zone is
   already on Cloudflare, so the DNS record is added for you).
5. Wait until the domain shows **Active**, then open
   `https://sivacom.sivayaantechnologies.com` — the SivaComm sign-in page.

Command-line alternative (development machine, after `npx wrangler login`):

```powershell
cd C:\src\SivaComm\dashboard
npx wrangler pages deploy dist --project-name sivacom-dashboard --branch main
```

### Sign in

`ops` / **S2**. The Overview should load and **Applications** should list
*Sivayaan HMS* with key **K1** and both templates.

Use the **custom domain**. The `*.pages.dev` address Cloudflare also gives
the project is a different site, so the browser will not send the login
cookie there and sign-in appears to do nothing.

---

## 9. Meta webhook

Meta for Developers → your app → **WhatsApp → Configuration → Webhook → Edit**:

| Field | Value |
|---|---|
| Callback URL | `https://sivacomapi.sivayaantechnologies.com/api/v1/webhooks/whatsapp` |
| Verify token | **S3** |
| Webhook fields | subscribe to `messages` |

**Verify and save** must succeed (it needs step 7 working and S3 identical in
both places). Skip this step until the `WhatsApp` values are filled in.

---

## 10. Point HMS_WEB at SivaComm

In HMS_WEB's `appsettings.Production.json` (beside its exe or in its IIS site
folder):

```json
"SivaComm": {
  "Enabled": true,
  "BaseUrl": "http://localhost:6071",
  "ClientId": "hms-web",
  "KeyId": "K1",
  "Secret": "S4",
  "TimeoutSeconds": 10
}
```

| Key | Value |
|---|---|
| `BaseUrl` | `http://localhost:6071` when HMS_WEB's API runs on this VM; `https://sivacomapi.sivayaantechnologies.com` when it runs elsewhere |
| `ClientId` | `hms-web` — SivaComm `Applications[0].Code` |
| `KeyId` / `Secret` | **K1** / **S4** — exactly as in SivaComm's file |

Deploy HMS_WEB's `feature/sivacomm-otp` build the usual way (its
`DEPLOY_IIS.md` / `RELEASE_POSTGRESQL.md`): migrate first — it adds the
`PasswordResetTransactions` table — then publish and restart. Push its
frontend so Cloudflare Pages rebuilds `healthone.sivayaantechnologies.com`
with the three-step reset page.

`"Enabled": false` (or no `SivaComm` section) keeps HMS_WEB on its normal
flow; switching back and forth is an edit and a restart.

---

## 11. Check it end to end

| # | Do | Expect |
|---|---|---|
| 1 | `https://sivacomapi.sivayaantechnologies.com/health/ready` | `ready` |
| 2 | Sign in at `https://sivacom.sivayaantechnologies.com` | Overview loads; Applications shows *Sivayaan HMS*, key K1, both templates |
| 3 | In HMS, register a test clinic with **your** mobile | dashboard → Messages: a **Welcome** row (Submitted → Delivered once WhatsApp values and webhook are set) |
| 4 | HMS sign-in → *Forgot password?* → the test username | code on WhatsApp; an **OTP** row in the dashboard |
| 5 | Wrong code, then the right one, then a new password | "4 tries left", then the password fields, then sign-in works with the new password |
| 6 | Dashboard → Overview | OTPs created 1, verified 1, failed checks 1, resets completed 1, welcome 1 |

---

## 12. Releasing a new version

**API** — build new zips (step 2), copy the API zip to the VM, then:

```powershell
& "C:\Program Files\PostgreSQL\18\bin\pg_dump.exe" -U sivacomm -Fc -f "C:\Server\sivacomapi-backups\sivacomm-$(Get-Date -Format yyyyMMdd-HHmm).dump" sivacomm
$rel = "C:\Deployments\sivacom\release-$(Get-Date -Format yyyyMMdd-HHmm)"
Expand-Archive C:\Users\<you>\Downloads\SivaComm-api-*.zip -DestinationPath $rel -Force
cd $rel; .\Install-SivaComm.ps1
```

The installer keeps `appsettings.Production.json`, `logs` and `keys`, applies
any new migration, and restarts the service.

**Dashboard** — Cloudflare → `sivacom-dashboard` → **Create deployment** →
drag in the new dashboard zip (or `npx wrangler pages deploy dist …`). The
custom domain switches to it as soon as it finishes.

**Roll back** — API: run `Install-SivaComm.ps1` from the previous
`C:\Deployments\sivacom\release-…` folder (restore the dump first if the newer release
migrated the database). Dashboard: Cloudflare → Deployments → the previous
one → **Rollback to this deployment**. HMS only: set its
`SivaComm:Enabled` to `false` and restart HMS — immediate.

**Config-only changes** (rotate a key, new template, add an application,
Meta token): edit `C:\Server\sivacomapi\appsettings.Production.json`, then
`Restart-Service SivaComApi`. Recipes: DEPLOYMENT.md §12.

---

## 13. When something is wrong

| Symptom | Cause | Fix |
|---|---|---|
| Installer: *ASP.NET Core 10 runtime is not installed* | runtime missing | step 3 |
| Installer: *REPLACE-ME value(s)* | config not finished | step 5 |
| Installer: migration fails with `28P01` / password authentication | `Database:Password` ≠ D1, or role missing | step 4/5 |
| Service stops right after start | read the newest file in `C:\Server\sivacomapi\logs` — the first error names the setting | — |
| `https://sivacomapi…` → 530 / 1033 | tunnel not connected | `Get-Service cloudflared`; check `config.yml` |
| `https://sivacomapi…` → 502 | the tunnel cannot reach SivaComApi: hostname type HTTPS instead of HTTP, URL not `127.0.0.1:6071`, connector on another machine, or `sivacomapi` also listed on another tunnel | step 7, "The one rule that matters" |
| `Invoke-WebRequest https://…` → *underlying connection was closed* | Windows PowerShell 5.1 using old TLS | `[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12` |
| `localhost:6071` → *400 Invalid Hostname* | an IIS site is bound to 6071 | remove that site; SivaComApi runs as a service, not under IIS |
| Dashboard: browser console shows *CORS* / *blocked by CORS policy* | `Cors:AllowedOrigins` ≠ the dashboard origin | exactly `https://sivacom.sivayaantechnologies.com`, restart SivaComApi |
| Dashboard: sign-in returns but you are still on the sign-in page | using the `*.pages.dev` address, or the API not on HTTPS | use the custom domain; API through the tunnel |
| Dashboard: everything fails with network errors | bundle built with the wrong `VITE_API_URL` | check `dashboard\.env.production`, rebuild, redeploy |
| Meta webhook verify fails | step 7 not working, or S3 differs | fix and retry |
| HMS resets all answer "try again in a moment" | HMS cannot reach SivaComm, or 401 (KeyId/Secret mismatch, clock) | HMS log: *SivaComm refused this application's signature*; compare K1/S4 in both files |

---

## Appendix: every value and where it goes

| Value | SivaComm `C:\Server\sivacomapi\appsettings.Production.json` | HMS_WEB `appsettings.Production.json` | Elsewhere |
|---|---|---|---|
| API port | `Urls`: `http://localhost:6071` | `SivaComm:BaseUrl` (same VM) | tunnel `config.yml` service |
| API public name | — | `SivaComm:BaseUrl` (other machine) | tunnel hostname; Meta callback URL; `dashboard\.env.production` |
| Dashboard origin | `Cors:AllowedOrigins` | — | Pages custom domain |
| D1 database password | `Database:Password` | — | PostgreSQL role |
| S1 master key | `Security:MasterKey` | — | your password manager, off the VM |
| S2 first admin password | `Admin:Password` | — | — |
| S3 webhook verify token | `WhatsApp:WebhookVerifyToken` | — | Meta webhook setup |
| Meta token / phone id / app secret | `WhatsApp:*` | — | Meta |
| Template names, languages | `Applications[0].Templates` | — | Meta (approved) |
| K1 key id | `Applications[0].Keys[0].KeyId` | `SivaComm:KeyId` | — |
| S4 signing secret | `Applications[0].Keys[0].Secret` | `SivaComm:Secret` | — |
| On/off for HMS | — | `SivaComm:Enabled` | — |
