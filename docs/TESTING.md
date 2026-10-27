# Testing messages from the server with SivaCommTest.exe

Step by step: send one of **your own approved WhatsApp templates** (the example
uses `test_account_creation`) from the Azure VM, and see it arrive on a phone.

`SivaCommTest.exe` plays the part of an application such as HMS_WEB. It signs
requests with the application's key and calls the real API, so a successful
test shows the whole path works: the API, the database, the worker, Meta and
the phone.

Contents:

1. [How a template is chosen](#1-how-a-template-is-chosen)
2. [Before you start](#2-before-you-start)
3. [Read your template's details in WhatsApp Manager](#3-read-your-templates-details-in-whatsapp-manager)
4. [Point the Welcome template at your template (server config)](#4-point-the-welcome-template-at-your-template)
5. [Install the test client](#5-install-the-test-client)
6. [Configure the test client](#6-configure-the-test-client)
7. [Run the tests](#7-run-the-tests)
8. [Read the result](#8-read-the-result)
9. [When it fails](#9-when-it-fails)
10. [Clean up](#10-clean-up)

---

## 1. How a template is chosen

An application never names a WhatsApp template. It asks for a **purpose**,
and the server's config maps each purpose to a template:

```
SivaCommTest.exe welcome  ──►  purpose "Welcome"           ──►  Applications[hms-web].Templates.Welcome.Name
SivaCommTest.exe otp      ──►  purpose "PasswordResetOtp"  ──►  Applications[hms-web].Templates.PasswordResetOtp.Name
```

So the test client's first word is always a **command** (`health`, `otp`,
`welcome`, `status`), never a template name. To send `test_account_creation`,
make it the `Welcome` template in the server config (step 4), then run
`welcome`.

| Your template's category | Use it as | Command |
|---|---|---|
| Utility or Marketing (any body, 0–10 `{{n}}` placeholders) | `Welcome` | `welcome` |
| Authentication (Meta's OTP template, with a Copy code button) | `PasswordResetOtp` | `otp` |

---

## 2. Before you start

On the VM, check each of these:

| Check | How |
|---|---|
| The service is running | `Get-Service SivaComApi` shows **Running** |
| The API answers | `Invoke-WebRequest http://localhost:6073/health/ready -UseBasicParsing` returns **200 ready** |
| Meta is configured | In `C:\Server\sivacomapi\appsettings.Production.json`, the `WhatsApp` section has `AccessToken` and `PhoneNumberId` filled in ([WHATSAPP_SETUP.md](WHATSAPP_SETUP.md)). **If they are empty, nothing is sent:** the server only logs the message and the test stops at SUBMITTED. |
| The phone may receive | With Meta's **test** number, the recipient (e.g. 919160494929) must be added under WhatsApp → API Setup → **To**. With your own verified business number, any WhatsApp number works. |

---

## 3. Read your template's details in WhatsApp Manager

Open **business.facebook.com → WhatsApp Manager → Message templates** and click
your template. Write down four things:

| Item | Where you see it | Example |
|---|---|---|
| **Name** | Title of the template | `test_account_creation` |
| **Language** | Language column, or the language tab inside the template. The code matters: *English* = `en`, *English (US)* = `en_US`, *English (UK)* = `en_GB` | `en` |
| **Number of placeholders** | Count `{{1}}`, `{{2}}`… in the **body**. Header and button placeholders are not supported. | `2` |
| **Status** | Must be **Active**. *In review* or *Rejected* cannot be sent. | Active – Quality pending |

Example body with 2 placeholders:

```
Hello {{1}}, your account {{2}} has been created.
```

Also note what each placeholder means (here: `{{1}}` = name, `{{2}}` = account)
so you can give sensible test values in step 6.

---

## 4. Point the Welcome template at your template

On the VM, open the server's config (Notepad as Administrator):

```powershell
notepad C:\Server\sivacomapi\appsettings.Production.json
```

Find `Applications` → the `hms-web` entry → `Templates` → `Welcome`, and
replace it with your template's details from step 3:

```json
"Templates": {
  "PasswordResetOtp": { "Name": "sivayaan_otp", "Language": "en" },
  "Welcome": { "Name": "test_account_creation", "Language": "en", "Parameters": 2 }
}
```

- `Name`: exactly as in WhatsApp Manager.
- `Language`: exactly the code from step 3.
- `Parameters`: the number of `{{n}}` in the body. Use `0` for none.

Save, then restart the service:

```powershell
Restart-Service SivaComApi
```

Check that it took the new template. The newest log file must show it:

```powershell
Get-ChildItem C:\Server\sivacomapi\logs\*.log | Sort-Object LastWriteTime | Select-Object -Last 1 |
    Select-String 'welcome template' | Select-Object -Last 1
```

Expected:

```
Application from configuration — hms-web: enabled, 1 live key(s), OTP template sivayaan_otp, welcome template test_account_creation
```

If the service does not start, the log says why (for example a `Parameters`
outside 0–10, or invalid JSON: a missing comma or quote).

> While `Welcome` points at your test template, **HMS_WEB's real welcome
> messages also use it**, if HMS_WEB is switched to SivaComm
> (`SivaComm:Enabled: true`). Step 10 puts it back.

---

## 5. Install the test client

1. Copy the zip `SivaCommTest-<date>-<commit>.zip` to the VM.
   - Build it on a development machine:
     `dotnet publish backend/tools/SivaComm.TestClient -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o artifacts/testclient`
2. Unzip it:

   ```powershell
   Expand-Archive C:\Deployments\sivacom\SivaCommTest-*.zip C:\Deployments\sivacom\test -Force
   cd C:\Deployments\sivacom\test
   dir
   ```

   You should see `SivaCommTest.exe`, `sivacomm-test.json` and `README.txt`.

It needs the .NET 10 runtime, which the VM already has for SivaComm.

---

## 6. Configure the test client

The test client needs the **same** `ClientId`, `KeyId` and `Secret` as the
`hms-web` entry in the server config. The safest way is to let PowerShell
copy them, so the secret is never typed or shown on screen.

Change the phone number and the welcome values to yours, then run in
`C:\Deployments\sivacom\test`:

```powershell
$server = Get-Content C:\Server\sivacomapi\appsettings.Production.json -Raw | ConvertFrom-Json
$app    = $server.Applications | Where-Object Code -eq 'hms-web'
$key    = $app.Keys | Where-Object { -not $_.Revoked } | Select-Object -First 1

$test = [ordered]@{
    BaseUrl           = 'http://localhost:6073'
    ClientId          = $app.Code
    KeyId             = $key.KeyId
    Secret            = $key.Secret
    Phone             = '919160494929'
    WelcomeParameters = @('Test User', 'TEST-001')   # one value per {{n}}, in order
}
$test | ConvertTo-Json | Set-Content .\sivacomm-test.json -Encoding utf8
"Template expects $($app.Templates.Welcome.Parameters) value(s); you gave $($test.WelcomeParameters.Count)."
```

The last line must show the same two numbers.

- **No placeholders:** use `WelcomeParameters = @()`.
- **One placeholder:** use `WelcomeParameters = @('Test User')`.

To edit the file by hand instead, it looks like this:

```json
{
  "BaseUrl": "http://localhost:6073",
  "ClientId": "hms-web",
  "KeyId": "hms-2026-10",
  "Secret": "<the Secret of hms-web's key in appsettings.Production.json>",
  "Phone": "919160494929",
  "WelcomeParameters": [ "Test User", "TEST-001" ]
}
```

| Setting | Value |
|---|---|
| `BaseUrl` | `http://localhost:6073` on the VM. To test through Cloudflare as well: `https://sivacomapi.sivayaantechnologies.com` |
| `ClientId`, `KeyId`, `Secret` | From `Applications` → `hms-web` → `Code` and `Keys` in the server config |
| `Phone` | Country code + number, digits only. Can also be given on the command line. |
| `WelcomeParameters` | Fills `{{1}}`, `{{2}}`… in order. The count must equal `Parameters` from step 4. |

---

## 7. Run the tests

In PowerShell, in `C:\Deployments\sivacom\test`:

### 7a. Health: service, database and key

```powershell
.\SivaCommTest.exe health
```

Expected:

```
OK  /health/live   200 ok
OK  /health/ready  200 ready
OK  signature      404 — credentials accepted
```

`signature 401` means `ClientId`, `KeyId` or `Secret` differs from the server
config: repeat step 6.

### 7b. Your template

```powershell
.\SivaCommTest.exe welcome 919160494929
```

Expected:

```
==> Send welcome (2 parameter(s): Test User, TEST-001)
OK  HTTP 202 QUEUED
   { "success": true, "status": "QUEUED", "messageId": "01a1…", ... }

==> Delivery status (up to 60 s; Ctrl+C to stop)
   00:00  QUEUED
   00:01  SUBMITTED
   00:03  SENT
   00:05  DELIVERED
OK  Delivered.
```

The message arrives on the phone with `{{1}}` = *Test User* and `{{2}}` = *TEST-001*.

### 7c. Optional: the OTP flow

For the Authentication template (`PasswordResetOtp`):

```powershell
.\SivaCommTest.exe otp 919160494929
```

Type the code from WhatsApp when asked. `r` resends it, and Enter cancels.
It then verifies the code, uses the grant and reports completion, as HMS_WEB
would.

### 7d. Look up a message later

```powershell
.\SivaCommTest.exe status 01a1xxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
```

Double-clicking the exe shows a menu with the same four choices.

---

## 8. Read the result

| Status | Meaning |
|---|---|
| `QUEUED` | Accepted by SivaComm, waiting for the worker (a second or two) |
| `SUBMITTED` | Handed to Meta, and Meta returned a message id. **With no WhatsApp settings on the server, it was only logged.** |
| `SENT` | Meta sent it to WhatsApp |
| `DELIVERED` | It reached the phone |
| `READ` | Opened on the phone (if the recipient has read receipts on) |
| `FAILED` | Refused or undeliverable. The reason is in step 9. |

`SENT`, `DELIVERED` and `READ` come from Meta's **webhook**. Without the
webhook ([WHATSAPP_SETUP.md](WHATSAPP_SETUP.md)), a message that arrived still
shows `SUBMITTED`. Check the phone itself.

You can see every test message in the dashboard,
https://sivacom.sivayaantechnologies.com → **Messages**, with its status and,
for failures, the **Error** column.

---

## 9. When it fails

Find the reason in either of these places:

- the dashboard → Messages → **Error** column
- the server log:

  ```powershell
  Get-ChildItem C:\Server\sivacomapi\logs\*.log | Sort-Object LastWriteTime | Select-Object -Last 1 |
      Select-String 'WhatsApp refused|Could not reach WhatsApp' | Select-Object -Last 5
  ```

| What you see | Cause | Fix |
|---|---|---|
| `HTTP 400 INVALID_REQUEST — The welcome template takes N parameters.` | `WelcomeParameters` count ≠ `Parameters` in the server config | Make them equal (step 4 and step 6) |
| `HTTP 400 INVALID_REQUEST — Each parameter must be 1–200 characters on one line.` | An empty or multi-line value in `WelcomeParameters` | Give every placeholder a short one-line value |
| `HTTP 400 INVALID_REQUEST — phone is not a number…` | Number not in international form | `91` + 10 digits, e.g. `919160494929` |
| `signature 401` / `HTTP 401` | Test client key ≠ server key | Step 6 again |
| Stays `SUBMITTED`, nothing on the phone, log says *No messaging provider is configured; message … was dropped* | `WhatsApp` section empty | Fill in AccessToken and PhoneNumberId, then restart |
| `(#132001) Template name does not exist in the translation` | Wrong `Name` **or wrong `Language`** (`en` ≠ `en_US`) | Copy both exactly from WhatsApp Manager, restart |
| `(#132000) Number of parameters does not match` | `Parameters` ≠ the number of `{{n}}` in the approved body | Recount, fix both files, restart |
| `(#132015)` / `(#132016)` template paused or disabled | Meta paused it for low quality | Check the template in WhatsApp Manager |
| `(#131030) Recipient phone number not in allowed list` | Using Meta's test number | Add the recipient under API Setup → To |
| `(#131026) Message undeliverable` | The number has no WhatsApp, or has blocked you | Try another number |
| `(#190)` / `401` from WhatsApp | AccessToken expired (the 24-hour token) | Use the permanent system-user token |
| `(#131047) Re-engagement message` | Free-form text outside 24 h. Should not occur with templates. | — |
| `Could not reach WhatsApp … timeout` | VM has no outbound internet to graph.facebook.com | Check the VM's outbound rules and proxy |
| `Could not reach …localhost:6073` | Service stopped | `Start-Service SivaComApi`, then check the log |

After changing `appsettings.Production.json`, always run
`Restart-Service SivaComApi`. The server reads it only at start.

---

## 10. Clean up

1. **Put the real welcome template back** in
   `C:\Server\sivacomapi\appsettings.Production.json`:

   ```json
   "Welcome": { "Name": "hms_welcome", "Language": "en", "Parameters": 2 }
   ```

   Then run `Restart-Service SivaComApi`. HMS_WEB sends exactly 2 values for
   welcome messages: clinic name, then admin username.

2. **Delete the test client's config**, which holds a real signing secret:

   ```powershell
   Remove-Item C:\Deployments\sivacom\test\sivacomm-test.json
   ```

   Recreate it with step 6 when you test again.
