# Configuring SivaComm to send messages

Everything that has to be set, in order, for each kind of message SivaComm
sends. Configuration lives in **two config files** plus Meta:

| Where | What | Who sets it |
|---|---|---|
| **Meta** (WhatsApp Manager) | the sending number, the access token, one approved template per message | once, by whoever owns the Meta Business account |
| **SivaComm config file** (`C:\Services\SivaComm\appsettings.Production.json`) | database, master key, Meta credentials, OTP rules, **and each application with its key and templates** | once per SivaComm install; edited for any change |
| **The application's config file** (e.g. HMS_WEB `appsettings.Production.json`) | the switch, SivaComm's address, the same key | once per application install |

Step-by-step deployment with commands: [DEPLOYMENT.md](DEPLOYMENT.md).

---

## The messages SivaComm can send today

| Message | `purpose` in SivaComm | Sent when | Meta template category | Parameters |
|---|---|---|---|---|
| **Password-reset code** | `PasswordResetOtp` | the application calls `POST /api/v1/otp/challenges` (user pressed *Forgot password*) | **Authentication** | exactly 1 — the code (SivaComm fills it in; the application never sees it) |
| **Welcome** | `Welcome` | the application calls `POST /api/v1/messages/welcome` (a new account was created) | **Utility** | as many as the template declares (0–10); the application sends them in order |

Each application chooses **its own template** for each purpose, so HMS and
Swarnakshi can word their welcome differently while sharing one WhatsApp
number. A third kind of message (an appointment reminder, say) is not a
configuration change today — it needs a new `MessagePurpose` value and an
endpoint in SivaComm. See the last section.

---

## 1 · Meta: one number, one token, one template per message

Full walkthrough in [WHATSAPP_SETUP.md](WHATSAPP_SETUP.md). What you come
away with:

| Value | Where in Meta | Goes into |
|---|---|---|
| Phone number **ID** (not the number) | WhatsApp → API Setup | `WhatsApp:PhoneNumberId` |
| **Permanent** access token | Business Settings → System Users → Generate token | `WhatsApp:AccessToken` |
| App secret | App settings → Basic | `WhatsApp:AppSecret` |
| Webhook verify token | you invent it, type it into Meta's webhook setup | `WhatsApp:WebhookVerifyToken` |

Then submit and get approved, in **WhatsApp Manager → Message templates**:

**a) The password-reset template** — one, shared by every application, or
one per application if you want different names.

| Field | Value |
|---|---|
| Category | **Authentication** |
| Name | e.g. `sivayaan_otp` |
| Language | e.g. English (`en`) |
| Code delivery | *Copy code* button |
| Options | add the security disclaimer and the expiry note ("expires in 10 minutes") if you like |

Meta writes the text itself ("*123456 is your verification code…*"); you
cannot add free wording to an Authentication template.

**b) A welcome template per application** — category **Utility**, your own
words, numbered placeholders. HMS's:

| Field | Value |
|---|---|
| Category | **Utility** |
| Name | `hms_welcome` |
| Language | English (`en`) |
| Body | `Welcome to Sivayaan HMS, {{1}}. Your admin username is {{2}} — sign in with the password you chose. Password reset codes will come to this number.` |
| Sample values | `{{1}}` Sunrise Clinic, `{{2}}` drsri@sunrise |

The language code you register in SivaComm must match the approved one
exactly — `en` and `en_US` are different templates to Meta.

---

## 2 · SivaComm server config

`appsettings.Production.json` beside `SivaComm.Api.exe` in the install
folder (default `C:\Services\SivaComm`), never committed. Start from
`deploy/appsettings.Production.json.template`, which lists every key with
notes. The essentials:

```json
{
  "Database": {
    "Host": "localhost",
    "Name": "sivacomm",
    "Username": "sivacomm",
    "Password": "<database password>"
  },

  "Security": {
    "MasterKey": "<32 random bytes, base64 — see below>"
  },

  "Admin": {
    "Username": "ops",
    "Password": "<first dashboard login; only used while no admin exists>"
  },

  "WhatsApp": {
    "AccessToken": "<permanent system-user token>",
    "PhoneNumberId": "<phone number ID>",
    "AppSecret": "<Meta app secret>",
    "WebhookVerifyToken": "<the string you typed into Meta>",
    "ApiVersion": "v21.0"
  },

  "Messaging": {
    "DefaultCountryCode": "91"
  }
}
```

Generate the master key once and keep a copy somewhere safe, **not** next
to the database backup — losing it makes stored application keys unreadable:

```powershell
$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```

`DefaultCountryCode` is put in front of a 10-digit number that has no country
code ("98765 43210" → "919876543210"). Leave it empty and such numbers are
refused instead of guessed.

**Leave `WhatsApp` empty** and nothing is sent: every message goes to the log
instead (with the code visible only in Development). That is the right
setting until Meta has approved the templates.

### Optional: the OTP and retry rules

These have working defaults (in `appsettings.json`); override only to change
policy. Reasons for each default are in [SECURITY.md](SECURITY.md).

```json
"Otp": {
  "Length": 6,
  "ExpiryMinutes": 10,
  "MaxAttempts": 5,
  "MaxSendsPerWindow": 3,
  "SendWindowMinutes": 15,
  "ResendCooldownSeconds": 60,
  "GrantExpiryMinutes": 10
},
"Worker": {
  "MaxAttempts": 5,
  "ProviderTimeoutSeconds": 10,
  "RecipientRetentionDays": 30,
  "RecordRetentionDays": 180
},
"RateLimits": {
  "CreatePerMinute": 60,
  "VerifyPerMinute": 300
}
```

### Meta webhook (delivery receipts)

WhatsApp → Configuration → Webhook: callback URL
`https://<sivacomm host>/api/v1/webhooks/whatsapp`, verify token as above,
subscribe to **messages**. Without it messages still send; the dashboard just
stops at *Submitted* instead of showing *Delivered* / *Read*.

---

## 3 · Each application, its key and its templates — in the same file

In SivaComm's config file, one `Applications` entry per consuming
application. Applied at every start; the database follows the file.

```json
"Applications": [
  {
    "Code": "hms-web",
    "Name": "Sivayaan HMS",
    "Enabled": true,
    "Keys": [
      { "KeyId": "hms-2026-10", "Secret": "<32+ random characters>", "Revoked": false }
    ],
    "Templates": {
      "PasswordResetOtp": { "Name": "sivayaan_otp", "Language": "en" },
      "Welcome":          { "Name": "hms_welcome",  "Language": "en", "Parameters": 2 }
    }
  }
]
```

| Key | Rule |
|---|---|
| `Code` | 2–40 lower-case letters, digits, hyphens; equals the application's `SivaComm:ClientId` |
| `Keys[].KeyId` / `Secret` | the **same** pair goes into the application's config file; secret ≥ 32 characters (generate as above) |
| `Templates.PasswordResetOtp` | the Authentication template; always 1 parameter (the code) |
| `Templates.Welcome.Parameters` | how many `{{n}}` the approved Utility template has; a welcome request with a different count is refused straight away rather than failing at Meta minutes later |

Repeat for each application (Swarnakshi, TransTrack) with its own `Code`,
its own key and its own welcome template; they can share the OTP template.

**Rotating a key:** add a second entry under `Keys` → restart SivaComm → put
the new pair in the application's file and restart it → set the old key
`"Revoked": true` → restart SivaComm. **Turning an application off:**
`"Enabled": false` — every call it makes is then refused.

A mistake in this section (bad code, duplicate key id, short secret, no live
key on an enabled application, missing template name) stops SivaComm at
start with every problem listed — not at the first message.

The dashboard's Applications page can make the same changes in an emergency;
for an application in the file, **the file wins at the next restart**.

---

## 4 · The application's config (HMS_WEB)

In HMS_WEB's `appsettings.Production.json`:

```json
"SivaComm": {
  "Enabled": true,
  "BaseUrl": "https://<sivacomm host>",
  "ClientId": "hms-web",
  "KeyId": "<key id from step 3>",
  "Secret": "<secret from step 3>",
  "TimeoutSeconds": 10
}
```

| `SivaComm:Enabled` | Password-reset code | Welcome message |
|---|---|---|
| `false` (default) | normal flow — HMS_WEB makes and checks the code itself, sends via its own `WhatsApp` section or the log | sent by HMS_WEB itself |
| `true` | **SivaComm** — template `PasswordResetOtp` | **SivaComm** — template `Welcome` |

Restart HMS_WEB after changing it (and run its migrations once — they add
`PasswordResetTransactions`). With `Enabled: true` and any of the other four
values missing, HMS_WEB refuses to start and says which section is
incomplete. Details: HMS_WEB `docs/PASSWORD_RESET.md`.

`PasswordReset:Enabled` in the same file is a separate, optional kill switch:
`false` removes self-service reset entirely (users ask their clinic admin),
whatever `SivaComm:Enabled` says.

---

## 5 · Checking it works

1. SivaComm: `https://<sivacomm host>/health/ready` answers `ready`.
2. Register a test clinic in HMS with your own number → a **Welcome** row
   appears in the dashboard's *Messages*.
3. Press *Forgot password* → a **OTP** row appears. Its *Delivery* column
   should move Queued → Submitted → (with the webhook) Delivered.
4. If a row says **Failed**, the *Error* column has Meta's code:

   | Error | Fix |
   |---|---|
   | `(#132001)` template does not exist | template name or language in step 3 does not match the approved one |
   | `(#190)` token expired | `WhatsApp:AccessToken` is the 24-hour token, not the permanent one |
   | `(#131030)` not in allowed list | still using Meta's test number |

---

## Adding another kind of message later

Today's two purposes are fixed in code (`MessagePurpose` in
`SivaComm.Data/Entities.cs`). A new one — appointment reminder, bill ready,
lab report ready — takes:

1. a new `MessagePurpose` value and a migration (the column is a string, so
   only the C# enum changes);
2. an endpoint like `POST /api/v1/messages/welcome` for it (or one generic
   `POST /api/v1/messages` taking the purpose — the better choice once there
   are three or more);
3. a dashboard template row for it;
4. a Utility template approved in Meta.

No change to the worker, the provider or the dashboard's reporting is
needed: they already handle any purpose.
