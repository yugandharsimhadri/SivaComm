# OTP, grant and credential rules

Every value here is in `appsettings.json` under `Otp`, `Security`, `Worker`
and `RateLimits`. Changing one is a security decision; this is why each
default is what it is.

## OTP

| Setting | Default | Why |
|---|---|---|
| `Otp:Length` | 6 | What people can retype from a lock-screen preview. Safety comes from the attempt limit, not the length. |
| `Otp:ExpiryMinutes` | 10 | Long enough for a slow delivery; short enough that a code seen on a screen last week is worthless. |
| `Otp:MaxAttempts` | 5 | One in 200,000 odds per challenge for a guesser. Malformed input counts as an attempt. |
| `Otp:MaxSendsPerWindow` / `SendWindowMinutes` | 3 / 15 | Stops the button being used to flood someone's phone at the business's expense. Counts resends too. |
| `Otp:ResendCooldownSeconds` | 60 | Same; also gives a slow message time to arrive before a second one confuses things. |
| `Otp:GrantExpiryMinutes` | 10 | The window between "code verified" and "password submitted". |

**Storage.** The OTP is never stored. The challenge row holds
`HMAC-SHA256(otpKey, challengeId + ":" + otp)`; `otpKey` is derived from the
master key, which is not in the database. A dump of the database therefore
cannot be brute-forced (10⁶ guesses are cheap against a plain or salted hash
of six digits; they are impossible without the key). This replaces HMS_WEB's
per-row PBKDF2, which does not protect a six-digit space.

The worker needs the code to send it, so it sits **encrypted** (AES-GCM) in
the outbox row's payload from the moment it is queued until the provider
accepts it or the message fails — then the payload is wiped.

**Atomicity.** Verify, resend, cancel and consume each run in one short
transaction holding `SELECT … FOR UPDATE` on the challenge or grant row.
Concurrent verifies of one challenge are applied one at a time: twenty
simultaneous correct submissions produce exactly one grant (tested), and
fifteen simultaneous wrong ones cannot push past the attempt limit (tested).
Create and resend additionally take a per-user advisory lock so two clicks
cannot both slip under the send cap.

**Binding.** A challenge is found only by `(id, application)` **and** must
carry the caller's `resetReference`. Anything else is `CHALLENGE_NOT_FOUND`
— the same answer as a nonexistent id, so one application cannot even learn
another's ids exist.

**Superseding.** A new challenge for the same user cancels earlier pending
ones; a resend replaces the code (old one dead) without resetting attempts.

## Grant

- 256 random bits, base64url, returned once. Stored as SHA-256 — sufficient
  for a value with that much entropy.
- One per challenge (unique index); single use; bound to the application and
  reset reference; 10-minute expiry; issued **only** on `VERIFIED`.
- Consumed with an `Idempotency-Key`. Same key = idempotent success (lost
  response recovery); different key = `ALREADY_USED` (replay). Cancelling the
  challenge expires an unspent grant.

## Application credentials

- One client per application, up to two live keys, revocable, rotation
  without downtime. Secrets are 256-bit random, shown once, stored AES-GCM
  encrypted (HMAC verification needs the secret, so it cannot be hashed).
- HMAC-SHA256 request signature over method, path, timestamp, nonce and body
  hash. ±5 minute clock skew. Nonces are stored (primary key per
  application) and a reuse fails atomically; they are swept after 30 minutes,
  well past the skew window.
- Unknown request fields are rejected, so no endpoint can be handed a
  password, even by mistake.

## Master key (`Security:MasterKey`)

32+ random bytes, base64. Two keys are derived from it with HKDF: one for
encryption (app secrets, phone numbers, queued payloads), one for OTP HMACs.
The API refuses to start outside Development without one, or with the
committed development key. **Losing it** makes stored app secrets and queued
messages unreadable: applications must be issued new keys, and queued
messages fail. Back it up with the same care as the database password, but
never in the same place as the database backup.

## What is never logged or shown

OTPs, grants, application secrets, provider tokens, passwords (never
received), full phone numbers, message payloads. Provider errors are
recorded as code + title only (a raw Meta body can echo the recipient). The
development log provider prints OTPs **only** when the environment is
Development; anywhere else it logs that a message was dropped.

## Retention

| Data | Kept |
|---|---|
| Request nonces | 30 minutes |
| Encrypted phone numbers on messages | 30 days (`Worker:RecipientRetentionDays`) |
| Challenges, grants, messages, events, audit | 180 days (`Worker:RecordRetentionDays`) |

## Dashboard

Separate cookie authentication (HttpOnly, SameSite=Strict, Secure outside
Development, 8 h sliding). State-changing admin calls require
`X-SivaComm-Admin: 1`, which a cross-site page cannot add without a CORS
preflight that this API never grants. Login is rate-limited per IP and
compares against a decoy hash for unknown usernames so timing does not reveal
which usernames exist. The first admin is created from configuration only
when none exists.
