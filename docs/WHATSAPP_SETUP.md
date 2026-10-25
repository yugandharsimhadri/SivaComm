# Connecting WhatsApp

The account setup is the same as HMS_WEB's `docs/WHATSAPP_SETUP.md` §1–2
(Meta Business account, an app with the WhatsApp product, business
verification, a real number, a **permanent System User token**). Do it once
here: every application then sends through this one number.

**Status:** the provider (`WhatsAppCloudProvider`) is written to Meta's Cloud
API template-message contract and covered by tests with a scripted provider,
but it has **not been run against a live WhatsApp account** — none is
configured. Treat the first real send as the real test.

## Templates

Business-initiated messages must be pre-approved templates.

**Password-reset OTP — category Authentication.** Meta fixes the wording
("*{{1}} is your verification code…*") and adds a copy-code button. SivaComm
sends the code as the single body parameter and again as the button's URL
parameter, which is the shape Authentication templates take. Create one per
application if you want the application's name in the template, or share one.

**Welcome — category Utility.** Free wording; parameters in order. HMS's is:

```
Welcome to Sivayaan HMS, {{1}}. Your admin username is {{2}} — sign in with the password you chose. Password reset codes will come to this number.
```

Register the names in the dashboard per application (Applications →
Templates). `LanguageCode` must match the approved language exactly
(`en` ≠ `en_US`).

## Configuration

```json
"WhatsApp": {
  "AccessToken": "EAAG… permanent system-user token",
  "PhoneNumberId": "123456789012345",
  "AppSecret": "from App settings → Basic",
  "WebhookVerifyToken": "a random string you choose",
  "ApiVersion": "v21.0"
}
```

With `AccessToken` or `PhoneNumberId` empty, messages go to the log instead
(and the startup log says so).

## Delivery receipts (webhook)

In the Meta app → WhatsApp → Configuration → Webhook:

- Callback URL: `https://<sivacomm host>/api/v1/webhooks/whatsapp`
- Verify token: the `WebhookVerifyToken` above
- Subscribe to **messages**

Every POST is checked against `X-Hub-Signature-256` with `AppSecret` before
it is read. Statuses `sent`, `delivered`, `read`, `failed` update the message
in the dashboard. They never affect OTP verification.

## Failures the worker reports

| Recorded error | Means | Retried? |
|---|---|---|
| `400 (#132001) …` | template name/language not approved as sent | no |
| `401 (#190) …` | token expired — still the 24-hour one | no |
| `400 (#131030) …` | test number, recipient not in its allowed list | no |
| `429 …`, `5xx …`, `timeout` | Meta busy or unreachable | yes, up to `Worker:MaxAttempts` with backoff |
| `could not decrypt` | master key changed since the message was queued | no |

An OTP whose challenge ends (expired, cancelled, superseded) before it is
sent is marked `Expired` and never sent.
