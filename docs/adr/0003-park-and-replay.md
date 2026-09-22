# 0003 — Events that arrive too early are parked and replayed

**Status:** accepted

## Context

The forwarder answers asynchronously, so events overtake each other. Two orderings happen in
practice:

- A driver marks a consignment delivered before the forwarder has confirmed the booking it belongs
  to.
- A status event for a shipment this service has not stored yet, because its submit is a few
  seconds behind on another partition.

Both events are valid. Neither can be acted on yet.

The two obvious responses are both wrong. Throwing means the bus retries and eventually
dead-letters a real delivery. Ignoring means losing it, silently.

## Decision

Park, then replay.

- The dispatcher parks the raw body of a status event whose shipment does not exist, under a blob
  prefix kept deliberately separate from the snapshot path so a parked event can never be mistaken
  for a shipment.
- `DeferredDeliveryHandler` appends a delivery it cannot act on to the snapshot's pending list.
- When a booking is confirmed, the callback handler **reports** the booking reference and the
  **dispatcher** drains both queues and re-dispatches each event.

The replay lives in the dispatcher, not in the handler, for two reasons. It is the dispatcher that
parked the events. And a handler that called the dispatcher back would be a dependency cycle — the
container refuses to build it, which is how the first version of this was found.

Both queues are cleared before anything is replayed: a replayed event that throws is retried by the
bus from the original message, and a queue that still held it would replay it twice.

## Consequences

- Nothing is lost and nothing is dead-lettered for being early.
- A shipment whose booking is never confirmed keeps its parked events indefinitely. That is
  visible in storage under the `pending/` prefix, and is the honest state — the events have not
  been handled.
- Parking without draining is the failure mode to watch: the shipment stops progressing and nothing
  fails. The replay path has its own tests for exactly that reason.
