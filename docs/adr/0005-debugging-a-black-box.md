# 0005 — Every outbound call is logged as a replayable curl

**Status:** accepted

## Context

The forwarder is someone else's service. When it rejects a call, the response says little, and the
only way to find out what it objected to is to reproduce the request by hand — usually hours later,
from a log, without the original payload.

## Decision

Every outbound call is logged as the equivalent `curl` command: method, full URL, headers and body,
with the authorization value replaced by the literal `[REDACTED]`. Debugging a rejected call becomes
a copy, a paste and an edit.

Two constraints on it:

- The token is never rendered. A token in log storage is a leaked token, and the only reliable way
  to keep it out is to never put it there. A test asserts the rendered string contains the
  redaction marker and no bearer value.
- The string is built only when the log level is actually enabled, so the cost is not paid in
  production when the level is off.

The correlation id — the bus message id — is on the same line and is sent as a header the forwarder
echoes back, so an outbound call, its callback and everything logged in between share one
identifier.

## Consequences

- Logs are more verbose at debug level. That is the trade, and it is the level nobody runs in
  production.
- A payload with personal data in it would end up in the log. This service's payloads carry venue
  and consignee identifiers only; a service whose payloads carried more would need a redaction
  policy for the body too, not just the token.
