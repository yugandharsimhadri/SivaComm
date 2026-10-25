# SivaComm — centralised WhatsApp OTP & messaging service

Status (2026-10-24): **steps 1–8 of §12 implemented for HMS_WEB**; Swarnakshi
and TransTrack Web deliberately deferred until HMS_WEB is proven. HMS_WEB's
changes are on its branch `feature/sivacomm-otp`, behind
`SivaComm:Enabled = true` (default `false`, unchanged behaviour).
Live WhatsApp delivery is **not** verified — no Meta credentials exist yet.

Where the build differs from this plan:

- Idempotency is carried by natural keys (`resetReference` per challenge,
  `clientReference` per welcome, the consume `Idempotency-Key` on the grant)
  instead of a generic `idempotency_records` table.
- Application secrets and payloads are encrypted with a configured master key
  (AES-GCM via HKDF), not ASP.NET Data Protection, so the key is explicit and
  portable between machines.
- The HMS client is a single file inside HMS_WEB (`SivaCommClient.cs`), not a
  shared package, matching HMS_WEB's "no shared references" rule. docs/API.md
  specifies the signature for the other applications.

The original plan follows unchanged.

---

## 1 · What already exists (HMS_WEB)

HMS_WEB has a working, single-application version of this feature. It is the
starting point, not something to throw away.

| Piece | Where | Reuse in SivaComm |
|---|---|---|
| Flow + security rationale | `HMS_WEB/docs/PASSWORD_RESET.md` | Rules carried over verbatim (stored phone only, uniform forgot-password answer, 6 digits / 10 min / 5 attempts / 3 sends per 15 min, new code kills old) |
| Meta setup runbook | `HMS_WEB/docs/WHATSAPP_SETUP.md` | Moves to SivaComm almost unchanged; HMS_WEB keeps a pointer |
| `IMessageSender`, `OutboundMessage`, `MessagePurpose` | `SivayaanHMS.Data/Messaging/IMessageSender.cs` | Becomes SivaComm's provider abstraction (`IMessageProvider`), extended to return a provider message id |
| `WhatsAppCloudMessageSender` + `TryNormalise` | `SivayaanHMS.Data/Messaging/WhatsAppCloudMessageSender.cs` | Ported; number-normalisation tests (`WhatsAppNumberTests.cs`) ported with it |
| `LoggingMessageSender` | same file | Kept as SivaComm's dev provider |
| `PasswordResetService` (issue/redeem) | `SivayaanHMS.Data/PasswordResetService.cs` | Logic split: OTP half moves to SivaComm; password half stays in HMS_WEB |
| `forgot-password` / `reset-password` | `SivayaanHMS.Api/Controllers/AuthController.cs` | Kept; re-wired to call SivaComm |
| `ForgotPasswordPage.tsx` | `HMS_WEB/frontend/src/pages` | Gains a third step (see gap G1) |

Stack is consistent across the candidates: **.NET 10, EF Core 10, Npgsql 10,
React + TypeScript + Vite**. SivaComm will match it.

## 2 · Conflicts and gaps found

| # | Finding | Consequence |
|---|---|---|
| G1 | HMS_WEB's reset screen takes **code + new password in one submit**, and the backend checks the code and sets the password in one call. | Conflicts with the requirement that password fields appear only after backend-authoritative OTP verification. Needs a verify step and a server-held grant. |
| G2 | OTP is stored as a **PBKDF2 hash with per-row salt**. | For a 6-digit code this does not protect a dump: 10⁶ guesses per row is cheap whatever the KDF. SivaComm uses **HMAC-SHA256 with a server-side pepper** (key not in the DB), which does. |
| G3 | Reset templates are **Utility** category. | Meta requires OTPs to use the **Authentication** category (copy-code button, no free text). The `hms_password_reset` template must be resubmitted as Authentication. |
| G4 | No delivery receipts; the message id from Meta is only logged. | SivaComm adds the webhook and stores provider message ids. |
| G5 | `whatsapp_reset.md` named in the brief does **not exist**. The closest file is `WHATSAPP_SETUP.md`. | I will update `PASSWORD_RESET.md` and `WHATSAPP_SETUP.md` unless you meant a different file. |
| G6 | "Three applications on .NET + React + PostgreSQL": candidates found — **HMS_WEB** (Postgres), **Swarnakshi** (Postgres), **TransTrack Web** (Postgres/SQLite, Next.js frontend). ABPS_WEB is SQL Server; ShiftPlanner is SQLite. Swarnakshi and TransTrack have **no self-service reset** today (TransTrack has admin-issued temporary passwords only). | Needs confirming. For apps with no reset flow, integration means building one — that is new work in those apps, not a re-wire. |
| G7 | Codes are never swept (noted in PASSWORD_RESET.md). | SivaComm adds a retention job. |

## 3 · Ownership split

```
 Browser ──► Source app backend ──(signed S2S)──► SivaComm API ──► outbox ──► worker ──► WhatsApp Cloud API
                │  owns: user lookup, stored phone,        owns: OTP, attempts, grant,        │
                │  reset transaction, password, sessions    message status, dashboard          ▼
                └◄──────────── synchronous verify result ◄──┘          Meta webhook ──► SivaComm (status only)
```

SivaComm **never** receives a password field — request DTOs have none, and the
JSON options reject unknown members so one cannot be smuggled in.

## 4 · Password-reset sequence (per source app)

1. **Forgot** — source backend resolves user + stored phone (unchanged
   enumeration-safe answer), creates its own `ResetTransaction` row, calls
   `POST /api/v1/otp/challenges` with `{ resetReference, userReference, phone, purpose }`.
   SivaComm returns `{ challengeId, status: "QUEUED", expiresAtUtc }`. The
   source stores `challengeId` against its transaction; the browser gets only an
   opaque reset-session cookie/token and the same generic sentence.
2. **Verify** — browser posts OTP to the source backend; source calls
   `POST /api/v1/otp/challenges/{challengeId}/verify` with `{ otp, resetReference }`.
   On `VERIFIED` SivaComm returns a 32-byte random `verificationGrant` (stored
   only as SHA-256), 10-min expiry. Source keeps the grant **server-side** and
   tells the browser "show new-password fields".
3. **Set password** — browser posts new password to the source backend.
   Source calls `POST /api/v1/grants/consume` with `{ grant, resetReference }`
   and `Idempotency-Key: <resetReference>`, then sets the password in its own
   DB, marks its transaction complete, revokes sessions, and finally
   `POST /api/v1/otp/challenges/{id}/completion` (reporting only).

**Lost response after consume:** consume is idempotent on the idempotency key —
a retry with the same key and grant returns the same `CONSUMED_OK` for 24 h,
so the source can retry safely. The source's password change is idempotent per
`ResetTransaction` (state `PasswordChanged`), so a retried submit after a lost
response returns success instead of a second change.

**Timeouts / 5xx from SivaComm are always failure** (`TEMPORARY_ERROR`) in the
source app; there is no "assume verified" path.

## 5 · Normalised verification outcomes

| Status | HTTP | Grant? | Source UI action |
|---|---|---|---|
| `VERIFIED` | 200 | yes | show new-password step |
| `INVALID_OTP` | 422 | no | retry, show `remainingAttempts` |
| `EXPIRED` | 422 | no | offer resend |
| `ATTEMPTS_EXCEEDED` | 422 | no | block; restart after cooldown |
| `ALREADY_USED` / `CANCELLED` | 409 | no | restart reset |
| `CHALLENGE_NOT_FOUND` | 404 | no | restart (shown to user as invalid code) |
| `RATE_LIMITED` | 429 | no | wait (`Retry-After`) |
| `TEMPORARY_ERROR` | 503 | no | retryable error |

One response DTO for all: `{ success, status, message?, remainingAttempts?, resetReference?, verificationGrant?, expiresAtUtc?, correlationId }`.

## 6 · Security settings (all configurable, defaults from HMS_WEB)

| Setting | Default |
|---|---|
| OTP length | 6 |
| OTP expiry | 10 min |
| Max verify attempts | 5 |
| Sends per user reference | 3 / 15 min |
| Resend cooldown | 60 s |
| Grant expiry | 10 min, single use |
| Per-app request rate | 60 / min (verify), 20 / min (create) |
| Request clock skew | ±5 min; nonce kept 10 min |

Verification is one short transaction: `SELECT … FOR UPDATE` on the challenge
row, check status/expiry/attempts/binding, constant-time HMAC compare,
increment or mark verified + insert grant, commit. Concurrent verifies of the
same challenge serialise on the row lock, so two correct submits produce one
grant and one `ALREADY_USED`.

## 7 · Application authentication

Each app gets a client id and a secret (two active keys for rotation, revocable
in the dashboard). Every request is **HMAC-SHA256 signed**:

```
X-SivaComm-Client: hms-web
X-SivaComm-Key-Id: k2
X-SivaComm-Timestamp: 2026-10-24T10:00:00Z
X-SivaComm-Nonce: <uuid>
X-SivaComm-Signature: base64(HMAC(secret, method \n path \n timestamp \n nonce \n sha256(body)))
```

Secrets are stored encrypted with ASP.NET Data Protection (HMAC needs the
secret, so it cannot be one-way hashed). The client identity comes **only** from
the verified signature; every query is filtered by it, so app B cannot read or
verify app A's challenges. A small `SivaComm.Client` NuGet-style project does
the signing so each app adds ~20 lines, not a reimplementation.

## 8 · Database (`sivacomm`, own database, schema `comm`)

`applications`, `application_keys`, `otp_challenges` (app_id, reset_reference,
user_reference, phone_masked, phone_enc, otp_hmac, attempts, status, expires_at,
UNIQUE(app_id, reset_reference)), `verification_grants` (grant_sha256 UNIQUE,
challenge_id, expires_at, consumed_at, consume_idempotency_key),
`messages` (outbox: status, attempts, next_attempt_at, provider_message_id
UNIQUE), `message_events` (provider event id UNIQUE → duplicate webhooks are
no-ops; out-of-order handled by status rank, delivered never regresses to sent),
`templates`, `idempotency_records`, `request_nonces`, `audit_events`,
`admin_users`. EF Core migrations, UTC `timestamptz`, check constraints on
status columns. A retention job deletes challenge material after 30 days and
nonces after 10 minutes.

## 9 · Worker

Hosted `BackgroundService` in the same process. Claims outbox rows with
`FOR UPDATE SKIP LOCKED`, bounded concurrency (4), provider timeout 10 s,
up to 5 attempts with exponential backoff + jitter, then `FAILED` (dead
letter, visible in the dashboard). Verification never waits on it.

## 10 · Endpoints (`/api/v1`)

| Endpoint | Caller |
|---|---|
| `POST otp/challenges` | app (signed) |
| `POST otp/challenges/{id}/resend` | app |
| `POST otp/challenges/{id}/verify` | app |
| `POST otp/challenges/{id}/cancel` | app |
| `POST otp/challenges/{id}/completion` | app |
| `POST grants/consume` | app |
| `POST messages/welcome` | app |
| `GET messages/{id}` | app (own only) |
| `GET/POST webhooks/whatsapp` | Meta (verify token / `X-Hub-Signature-256`) |
| `GET admin/summary`, `GET admin/messages`, `admin/applications/*` | dashboard (cookie auth, admin role) |
| `GET health/live`, `GET health/ready` | ops |

## 11 · Dashboard

React + Vite, served by the API. Cookie-authenticated admin login (no secrets in
the browser). Pages: Overview (per-app totals, status breakdown, OTP created /
verified / failed, resets completed, welcome count, backlog, recent failures,
provider health), Messages (paginated, filters, masked phones), Applications
(register, rotate/revoke keys — secret shown once).

## 12 · Build order

1. Solution skeleton, DB + migrations, app auth (signing middleware) + tests
2. OTP challenge create, outbox, logging provider
3. Verify + grant + consume (atomicity / concurrency / replay tests)
4. WhatsApp provider port + webhook
5. `SivaComm.Client` library
6. HMS_WEB integration behind `SivaComm:Enabled = false | true` (false stays
   default, so current behaviour and deployment are unchanged until switched);
   3-step reset page; docs updated
7. Dashboard
8. Welcome messages via SivaComm
9. Other two apps

Live WhatsApp delivery cannot be claimed working until real Meta credentials
and an approved Authentication template are configured and tested.
