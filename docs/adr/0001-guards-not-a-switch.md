# 0001 — Handlers with guards, not a switch on event type

**Status:** accepted

## Context

Six kinds of event drive one lifecycle: a submit, a status change, and four confirmations from the
forwarder. The obvious implementation is a switch on the event type.

It does not survive contact with the domain. "Delivered" means *send the delivery to the
forwarder* when the booking has been confirmed, and *keep this for later* when it has not. The
event is identical in both cases; only the shipment's stored state distinguishes them. A switch on
event type has to rediscover that distinction inside each arm, and the arms grow a second
dimension until nobody can see which combination is handled where.

## Decision

Every handler declares `CanHandle(snapshot, workflowEvent)` and the dispatcher takes the first one
that claims the event. The guard sees the stored snapshot as well as the event, so the condition
lives in one expression instead of being spread through a branch body.

Registration order in `Program.cs` is priority order, and the file says so at the line where it
matters. `DeliverySubmissionHandler` and `DeferredDeliveryHandler` are adjacent and their guards
are exact inverses; a test asserts that, because if they ever overlap an event is handled twice,
and if they ever gap one is silently dropped.

An event nothing claims is logged and completed, not thrown. So is an operation type this build
does not recognise. The queue carries events for more than this service, and failing on something
that was never ours dead-letters a perfectly good message.

## Consequences

- Adding a lifecycle step is one class and one registration line.
- Registration order is behaviour. Reordering it is a change, and the comment in `Program.cs` says
  which line matters and why.
- A guard that is subtly wrong fails by declining, which is quiet. That is why the inverse-guard
  test exists and why the dispatcher logs an unclaimed event rather than ignoring it.
