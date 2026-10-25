# Deploying SivaComm: API on the Azure VM, dashboard on Cloudflare

Step by step, from two zip files to a working service. Every setting is in a
config file; nothing is typed into the application.

| | Address | Runs on |
|---|---|---|
| **Dashboard** | `https://sivacom.sivayaantechnologies.com` | Cloudflare Pages (static files) |
| **API, public** | `https://sivacomapi.sivayaantechnologies.com` | Cloudflare Tunnel → the VM |
| **API, on the VM** | `http://localhost:6071` | Windows service `SivaCommApi`, folder `C:\SivaComm\api` |
| **Database** | `localhost:5432`, database `sivacomm`, role `sivacomm` | PostgreSQL on the VM |

```
 Operator browser ──https──> sivacom.sivayaantechnologies.com        (Cloudflare Pages: dashboard files)
        │
        └──── API calls, with the login cookie ────┐
                                                  v
 Meta webhook ──https──> sivacomapi.sivayaantechnologies.com  (Cloudflare edge, TLS)
                                                  │  tunnel — outbound from the VM, no open ports
                                                  v
 Azure VM:   cloudflared ──> http://localhost:6071  SivaCommApi service ──> PostgreSQL localhost:5432
                                   ^
 HMS_WEB API ──signed calls────────┘   (http://localhost:6071 if HMS is on this VM,
                                        https://sivacomapi.… if it is elsewhere)
```

- [1. Before you start](#1-before-you-start)
- [2. Build the two zips](#2-build-the-two-zips-development-machine)
- [3. Prepare the VM](#3-prepare-the-vm-once)
- [4. Database](#4-database-once)
- [5. Copy the API zip and fill in the config](#5-copy-the-api-zip-and-fill-in-the-config)
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
| `SivaComm-api-….zip` | the Azure VM | `api\` (the program), `Install-SivaComm.ps1`, `appsettings.Production.json.template`, `cloudflared\config.yml.template`, this guide, `VERSION.txt` |
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
New-Item -ItemType Directory -Force C:\SivaComm\api, C:\SivaComm\logs, C:\SivaComm\keys, C:\SivaComm\backups | Out-Null
```

| Folder | Holds | Replaced on upgrade? |
|---|---|---|
| `C:\SivaComm\api` | the program + `appsettings.Production.json` | program yes, config **never** |
| `C:\SivaComm\release-<date>` | each unzipped release | a new one per release |
| `C:\SivaComm\logs` | daily log files | no |
| `C:\SivaComm\keys` | dashboard login-cookie keys | no |
| `C:\SivaComm\backups` | database dumps | no |

---

## 4. Database (once)

Choose a strong password for the application role — call it **D1**.

```powershell
$psql = "C:\Program Files\PostgreSQL\18\bin\psql.exe"   # adjust 18 to the installed version
& $psql -U postgres -c "CREATE ROLE sivacomm LOGIN PASSWORD 'D1';"
& $psql -U postgres -c "CREATE DATABASE sivacomm OWNER sivacomm ENCODING 'UTF8' TEMPLATE template0;"
```

(`-U postgres` asks for the PostgreSQL superuser password chosen when
PostgreSQL was installed.) The tables are created by the installer's
migration step. The role does **not** get `CREATEDB`.

Using **Azure Database for PostgreSQL** instead of PostgreSQL on the VM?
Create the same role and database there, and in step 5 set `Database:Host`
to its server name and `Database:SslMode` to `Require`.

---

## 5. Copy the API zip and fill in the config

**Copy** `SivaComm-api-….zip` to the VM (RDP clipboard / drive redirection,
or a storage blob), then:

```powershell
$rel = "C:\SivaComm\release-$(Get-Date -Format yyyyMMdd)"
Expand-Archive C:\Users\<you>\Downloads\SivaComm-api-*.zip -DestinationPath $rel -Force
cd $rel
Get-Content .\VERSION.txt
.\Install-SivaComm.ps1
```

On the first run the installer copies the template to
`C:\SivaComm\api\appsettings.Production.json` and stops:

```
!!  No configuration yet. A template was copied to:
!!      C:\SivaComm\api\appsettings.Production.json
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
notepad C:\SivaComm\api\appsettings.Production.json
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
| `DataProtection:KeysPath` | `C:\\SivaComm\\keys` *(already set)* |
| `Logging:File:PathFormat` | `C:\\SivaComm\\logs\\sivacomm-{Date}.log` *(already set)* |

In JSON, every backslash in a path is written twice (`C:\\SivaComm\\keys`).

**Lock the file** to administrators and the service account:

```powershell
icacls C:\SivaComm\api\appsettings.Production.json /inheritance:r /grant:r "Administrators:F" "SYSTEM:R"
```

---

## 6. Install the API

Same folder, same window:

```powershell
.\Install-SivaComm.ps1
```

```
==> Stopping SivaCommApi                      (not on the first install)
==> Copying the build to C:\SivaComm\api (keeping appsettings.Production.json)
==> Applying database migrations
Applying 1: 20261024155431_Initial
==> Creating service SivaCommApi
==> Starting SivaCommApi
==> Ready: http://localhost:6071/health/ready
```

What it refuses, with the reason: a missing ASP.NET Core 10 runtime, invalid
JSON, any `REPLACE-ME` left, a database it cannot reach, and — from the
application itself — a missing or development master key, or an invalid
`Applications` entry (every problem listed at once).

**Check on the VM, before any tunnel:**

```powershell
Invoke-WebRequest http://localhost:6071/health/ready -UseBasicParsing | Select StatusCode, Content
Get-Content (Get-ChildItem C:\SivaComm\logs\*.log | Sort LastWriteTime | Select -Last 1).FullName -Tail 20
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

### If HMS_WEB's tunnel already runs on this VM (usual case)

1. Open its config:
   ```powershell
   notepad C:\Windows\System32\config\systemprofile\.cloudflared\config.yml
   ```
2. Add this rule to `ingress:`, **above** the last `- service: http_status:404` line:
   ```yaml
     - hostname: sivacomapi.sivayaantechnologies.com
       service: http://localhost:6071
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

`200 ready` is success. `530`/`1033`: the tunnel is not connected. `502`:
the tunnel is up but nothing answers on 6071 (step 6).

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
& "C:\Program Files\PostgreSQL\18\bin\pg_dump.exe" -U sivacomm -Fc -f "C:\SivaComm\backups\sivacomm-$(Get-Date -Format yyyyMMdd-HHmm).dump" sivacomm
$rel = "C:\SivaComm\release-$(Get-Date -Format yyyyMMdd-HHmm)"
Expand-Archive C:\Users\<you>\Downloads\SivaComm-api-*.zip -DestinationPath $rel -Force
cd $rel; .\Install-SivaComm.ps1
```

The installer keeps `appsettings.Production.json`, `logs` and `keys`, applies
any new migration, and restarts the service.

**Dashboard** — Cloudflare → `sivacom-dashboard` → **Create deployment** →
drag in the new dashboard zip (or `npx wrangler pages deploy dist …`). The
custom domain switches to it as soon as it finishes.

**Roll back** — API: run `Install-SivaComm.ps1` from the previous
`C:\SivaComm\release-…` folder (restore the dump first if the newer release
migrated the database). Dashboard: Cloudflare → Deployments → the previous
one → **Rollback to this deployment**. HMS only: set its
`SivaComm:Enabled` to `false` and restart HMS — immediate.

**Config-only changes** (rotate a key, new template, add an application,
Meta token): edit `C:\SivaComm\api\appsettings.Production.json`, then
`Restart-Service SivaCommApi`. Recipes: DEPLOYMENT.md §12.

---

## 13. When something is wrong

| Symptom | Cause | Fix |
|---|---|---|
| Installer: *ASP.NET Core 10 runtime is not installed* | runtime missing | step 3 |
| Installer: *REPLACE-ME value(s)* | config not finished | step 5 |
| Installer: migration fails with `28P01` / password authentication | `Database:Password` ≠ D1, or role missing | step 4/5 |
| Service stops right after start | read the newest file in `C:\SivaComm\logs` — the first error names the setting | — |
| `https://sivacomapi…` → 530 / 1033 | tunnel not connected | `Get-Service cloudflared`; check `config.yml` |
| `https://sivacomapi…` → 502 | nothing on `localhost:6071` | `Get-Service SivaCommApi`; `Urls` in config |
| Dashboard: browser console shows *CORS* / *blocked by CORS policy* | `Cors:AllowedOrigins` ≠ the dashboard origin | exactly `https://sivacom.sivayaantechnologies.com`, restart SivaCommApi |
| Dashboard: sign-in returns but you are still on the sign-in page | using the `*.pages.dev` address, or the API not on HTTPS | use the custom domain; API through the tunnel |
| Dashboard: everything fails with network errors | bundle built with the wrong `VITE_API_URL` | check `dashboard\.env.production`, rebuild, redeploy |
| Meta webhook verify fails | step 7 not working, or S3 differs | fix and retry |
| HMS resets all answer "try again in a moment" | HMS cannot reach SivaComm, or 401 (KeyId/Secret mismatch, clock) | HMS log: *SivaComm refused this application's signature*; compare K1/S4 in both files |

---

## Appendix: every value and where it goes

| Value | SivaComm `C:\SivaComm\api\appsettings.Production.json` | HMS_WEB `appsettings.Production.json` | Elsewhere |
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
