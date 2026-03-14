# Gmail SMTP setup

The worker sends email through any SMTP server. This page covers the one-time setup required to use Gmail.

## Prerequisites

Gmail does not accept your regular account password for third-party SMTP clients. You must use an **App Password**, which requires **2-Step Verification** to be enabled on the sending Google Account.

If 2-Step Verification is not yet active, enable it at [myaccount.google.com/security](https://myaccount.google.com/security) before continuing.

## Create an App Password

Follow Google's guide to generate an App Password:
[support.google.com/accounts/answer/185833](https://support.google.com/accounts/answer/185833)

Name the password something like `TalkingPointsSummary` so it is easy to identify later. Google will show the 16-character password once — copy it before closing the dialog.

## Configuration values

| Setting | Value |
| --- | --- |
| `Smtp:Host` | `smtp.gmail.com` (default — no change needed) |
| `Smtp:Port` | `587` (default — no change needed) |
| `Smtp:Username` | Your full Gmail address, e.g. `yourname@gmail.com` |
| `Smtp:Password` | The 16-character App Password from the step above |
| `Smtp:FromEmail` | Same Gmail address as `Smtp:Username` |

`Smtp:FromEmail` must match the authenticated address. Gmail rejects outbound messages where the `From` header does not match the authenticated account.

## Verify the connection

Run `check-config` after setting the values to confirm the worker connects and authenticates successfully:

```bash
docker exec talking-points-summary dotnet TalkingPointsSummary.dll check-config
```

A passing result looks like:

```text
✅ PASS  SMTP connectivity  Connected and authenticated to smtp.gmail.com:587
```

If you see an authentication failure, double-check that you copied the App Password correctly and that 2-Step Verification is still active on the account.
