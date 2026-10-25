# SivaComm API, v1

For application **backends**. Nothing here may be called from a browser: the
signing secret would have to ship in the bundle, and the browser would then
be trusted to say which user and phone a code is for.

---

## Authentication: a signature on every request

Each application is registered in the dashboard and gets a **client id**
(e.g. `hms-web`), a **key id** and a **secret** (shown once). Up to two keys
can be live, for rotation without downtime: issue a new key, deploy it,
revoke the old one.

Every request carries:

| Header | Value |
|---|---|
| `X-SivaComm-Client` | client id |
| `X-SivaComm-Key-Id` | key id |
| `X-SivaComm-Timestamp` | now, ISO 8601 UTC, e.g. `2026-10-24T10:00:00.000Z` |
| `X-SivaComm-Nonce` | 16–64 random characters, never reused |
| `X-SivaComm-Signature` | see below |

```
canonical = METHOD + "\n" + PATH_AND_QUERY + "\n" + TIMESTAMP + "\n" + NONCE + "\n" + lowercase_hex(SHA-256(body bytes))
signature = base64( HMAC-SHA256( UTF-8(secret), UTF-8(canonical) ) )
```

`PATH_AND_QUERY` is exactly what reaches SivaComm, e.g.
`/api/v1/otp/challenges/0192…/verify`. An empty body hashes the empty string.

Refused with `401 UNAUTHORIZED` (one answer for every cause, so a caller
learns nothing about which): missing headers, unknown client or key, revoked
key, disabled application, timestamp more than 5 minutes off, wrong
signature, a nonce already seen. The application identity used for
everything afterwards comes **only** from this check — never from a body field.

Reference implementation: HMS_WEB `SivayaanHMS.Data/Messaging/SivaCommClient.cs`.

Optional: `X-Correlation-Id` (8–64 of `[A-Za-z0-9_-]`) is echoed back and
written into SivaComm's logs, so one id follows a request across both.

---

## Response shape

Every application endpoint answers:

```json
{
  "success": false,
  "status": "INVALID_OTP",
  "message": "The OTP is invalid. Please try again.",
  "remainingAttempts": 2,
  "correlationId": "4f0c…"
}
```

Fields are omitted when they do not apply. `verificationGrant` appears only
with `VERIFIED`.

| status | HTTP | Meaning |
|---|---|---|
| `QUEUED` | 202 | Accepted for sending. **Not** a delivery confirmation. |
| `VERIFIED` | 200 | OTP correct; `verificationGrant` issued |
| `CONSUMED` | 200 | Grant spent (or `replayed: true` — same Idempotency-Key retried) |
| `COMPLETED` | 200 | Completion recorded |
| `CANCELLED` | 200 from `/cancel`; 409 elsewhere | Challenge cancelled or superseded |
| `INVALID_OTP` | 422 | Wrong code; `remainingAttempts` |
| `EXPIRED` | 422 | Code or grant expired |
| `ATTEMPTS_EXCEEDED` | 422 | Locked; start a new challenge |
| `ALREADY_USED` | 409 | Challenge already verified, or grant consumed by another key |
| `RATE_LIMITED` | 429 | `retryAfterSeconds` and `Retry-After` |
| `CHALLENGE_NOT_FOUND` / `GRANT_NOT_FOUND` | 404 | Includes "belongs to another application" and "wrong reset reference" |
| `CONFLICT` | 409 | Reference reused with different bound data |
| `INVALID_REQUEST` | 400 | Malformed body, **unknown field**, no template configured |
| `TEMPORARY_ERROR` | 503 | Database or infrastructure fault — retry |
| `INTERNAL_ERROR` | 500 | Bug; logged with the correlation id |

**A caller must treat every non-2xx, every timeout and every unreadable
response as failure.** There is no response that should ever be read as
"probably verified".

---

## Endpoints

### `POST /api/v1/otp/challenges` — create and send an OTP

```json
{ "resetReference": "hms-6f1c…", "userReference": "u-1b2c…", "phone": "+91 98765 43210", "purpose": "PASSWORD_RESET" }
```

- `resetReference`: the application's own reset transaction id; 8–100 of
  `[A-Za-z0-9._:-]`; unique per application. **Idempotent**: the same
  reference and user again returns the same challenge and sends nothing more;
  the same reference with another user is `CONFLICT`.
- `userReference`: pseudonymous — the application's internal user id, never
  a username, email or phone.
- `phone`: the number **already on the account**. Normalised to digits with
  a country code (`Messaging:DefaultCountryCode` is prepended to a bare
  national number; with none configured, such numbers are refused).
- Supersedes any pending challenge for the same user.
- Limits: 3 sends per user per 15 minutes (`RATE_LIMITED`), plus a coarse
  per-application rate.

`202` → `{ status: "QUEUED", challengeId, resetReference, expiresAtUtc, resendAvailableAtUtc }`.
The OTP is never returned.

### `POST /api/v1/otp/challenges/{challengeId}/resend`

`{ "resetReference": "…" }`. A new code on the same challenge; the old code
stops working, the expiry restarts, the attempt counter does **not**. 60 s
cooldown and the per-user cap apply (`RATE_LIMITED`).

### `POST /api/v1/otp/challenges/{challengeId}/verify`

`{ "resetReference": "…", "otp": "123456" }`. Synchronous — never waits on
delivery. On success:

```json
{ "success": true, "status": "VERIFIED", "challengeId": "…", "resetReference": "…",
  "verificationGrant": "x7Yq…", "expiresAtUtc": "2026-10-24T10:20:00Z", "correlationId": "…" }
```

Keep the grant **server-side**. It is single-use, bound to this application
and this reset reference, and expires in 10 minutes. If this response is lost
the challenge is already spent: start a new challenge.

### `POST /api/v1/grants/consume`

`{ "grant": "x7Yq…", "resetReference": "…" }` with header
**`Idempotency-Key`** (8–100 chars; use the reset reference).

- First call: `200 CONSUMED` with `challengeId`, `resetReference`,
  `userReference` — check both references match yours before changing the
  password.
- Same key again (a retry after a lost response): `200 CONSUMED`, `replayed: true`.
- Any other key: `409 ALREADY_USED`. Expired: `422 EXPIRED`. Wrong
  application or reference: `404 GRANT_NOT_FOUND`.

Concurrent calls are serialised on the grant row; exactly one wins.

### `POST /api/v1/otp/challenges/{challengeId}/completion`

`{ "resetReference": "…", "outcome": "PASSWORD_CHANGED" }`. Reporting only,
for the dashboard; accepted after the grant was consumed; idempotent.

### `POST /api/v1/otp/challenges/{challengeId}/cancel`

`{ "resetReference": "…" }`. Cancels a pending or verified challenge and
kills an unspent grant. `200 CANCELLED`.

### `POST /api/v1/messages/welcome`

```json
{ "clientReference": "welcome-…", "phone": "…", "parameters": ["Sunrise Clinic", "drsri@sunrise"] }
```

Idempotent on `clientReference`. Parameter count must match the
application's welcome template. `202 QUEUED` with `messageId`.

### `GET /api/v1/messages/{messageId}`

The delivery status of one of **your** messages (another application's id is
`404`): `Queued → Submitted → Sent → Delivered → Read`, or `Failed` /
`Expired`. Delivery status says nothing about whether an OTP was verified.

### `GET|POST /api/v1/webhooks/whatsapp`

For Meta only. `GET` is the subscription handshake (`hub.verify_token`);
`POST` must carry a valid `X-Hub-Signature-256`. Duplicates are ignored;
out-of-order events never move a status backwards.

### Health

`GET /health/live` — process up. `GET /health/ready` — database reachable
and the outbox worker looping. Both anonymous, both bodiless on failure.

---

## Admin API (dashboard only)

Cookie session from `POST /api/admin/auth/login`; every request must carry
`X-SivaComm-Admin: 1`. Application keys do not work here and the admin
cookie does not work on `/api/v1`.

`GET /api/admin/summary?from&to&applicationId` · `GET /api/admin/messages?applicationId&status&purpose&from&to&page&pageSize` ·
`GET /api/admin/errors` · `GET|POST /api/admin/applications` ·
`POST /api/admin/applications/{id}/keys` · `POST /api/admin/applications/{id}/keys/{keyId}/revoke` ·
`PUT /api/admin/applications/{id}/templates` · `PUT /api/admin/applications/{id}/active`

Phone numbers are masked (`91••••••3210`) in everything these return; no
endpoint returns an OTP, a grant, a payload or a secret (except a new secret,
once, in the response that created it).
