# AGENTS.md

The reference behind `CLAUDE.md`: the lifecycle step by step, what is stored where, how it deploys,
and what is knowingly unfinished.

## The lifecycle

```
submit ─▶ AccountCreationPending ─▶ [forwarder: create account]
                                          │
                       account callback ──┴─▶ AccountCreated ─▶ BookingSubmitted
                                                                      │
                                              booking callback ───────┴─▶ BookingConfirmed
                                                                              │
                                                       ┌──────────────────────┤
                                          pickup callback              delivery event
                                                 │                            │
                                          PickupConfirmed              DeliverySubmitted
                                                                              │
                                                     delivery callback ───────┴─▶ DeliveryConfirmed
```

Every arrow marked *callback* is the forwarder answering out of band, minutes or hours later.

### Submit

`SubmitShipmentHandler` claims any submit command. If the consignee's account is already confirmed
it books immediately. Otherwise it writes `AccountCreationPending` to **both** stores *before*
calling the forwarder, and marks `AccountCreationFailed` and rethrows if that call fails. Writing
after the call would leave a request in flight that no stored state accounts for, and the retry
would create a second account for one consignee.

It also checks whether another shipment for the same consignee has already asked. The forwarder
does not deduplicate account creation.

### Account callback

`AccountCallbackHandler` confirms the account, books **its own shipment first**, then fans out over
every other shipment for that consignee still sitting in `AccountCreationPending` and books those
too. The "its own first" step is easy to miss — by the time the fan-out query runs, this shipment's
row has already left `AccountCreationPending`, so the query does not include it. Without that line
the shipment that triggered account creation is the only one that never gets booked, and nothing
fails to say so. It has its own test.

### Delivery, early or on time

Two guards, exact inverses:

- `DeliverySubmissionHandler` claims a delivery when the booking is confirmed (or pickup is, or the
  last delivery attempt failed) and it has not already been submitted.
- `DeferredDeliveryHandler` claims everything else and appends the command to the snapshot.

The dispatcher separately parks the raw body of any status event whose shipment does not exist yet.

### Booking callback and replay

`BookingCallbackHandler` confirms the booking and **returns the booking reference**; the dispatcher
drains both parked queues and re-dispatches each event. Replay lives in the dispatcher because the
dispatcher is what parked them — and because a handler calling the dispatcher back is a dependency
cycle the container refuses to build. See `docs/adr/0003`.

## Storage

| Where | What | Key |
| --- | --- | --- |
| Blob `shipments/{source}/{ref}.json` | The canonical snapshot | normalized reference |
| Blob `index/{forwarderRef}.json` | Forwarder reference → source + reference | the forwarder's own id |
| Blob `pending/{ref}/{ticks}.json` | Events parked before their shipment existed | reference + arrival time |
| Table `shipmentlifecycle` | Queryable lifecycle row | PK `{source}\|{consignee}`, RK reference |

The index blob exists because callbacks carry only the forwarder's reference. Without it a callback
cannot be matched to anything and the lifecycle stalls at whatever step last went out.

The `pending/` prefix is deliberately separate from the snapshot path: a parked event must never be
mistaken for a shipment that exists.

## Failure policy

Different by pipeline, on purpose:

| Situation | What happens | Why |
| --- | --- | --- |
| Handler throws | Propagates out of the trigger | Service Bus retry and dead-lettering is the mechanism; a second one in code is one more thing to maintain |
| Unknown operation type | Logged, message completed | The queue carries events for more than this service |
| No handler claims the event | Logged, message completed | Same reason |
| Status event before its shipment | Parked | It is valid, just early |
| Callback matches no shipment | Logged, message completed | The forwarder would otherwise redeliver forever something it cannot resolve |
| Forwarder rejects a call | State written as `*Failed`, then rethrown | The stored state and the retry both matter |

## Configuration

One bound options class, validated with `ValidateDataAnnotations().ValidateOnStart()`. Bus
connection and entity names stay as platform settings because the bindings read them directly via
`%Setting%` syntax.

Local values live in `local.settings.json`, which is gitignored for real secrets and carries only
emulator and stub values here.

## Deployment

`infra/functions/main.bicep` provisions the Function App and its Application Insights, and
references an **existing** storage account and App Service plan rather than creating them. A plan
per service is the most common avoidable cost in a Functions estate, and storage outlives any one
deployment. Storage keys are resolved at deploy time with `listKeys()`; secrets are `@secure()`
parameters read from the environment.

Service Bus entities are not in the template — see `docs/adr/0006`.

CI builds, runs the tests with Azurite installed, compiles the Bicep and every parameter file, and
runs a filesystem vulnerability scan, on pull requests as well as `main`.

## Known gaps

| Not built | Why that is fine here | The work it would take |
| --- | --- | --- |
| No integration test across two handlers | The dispatcher tests cover routing and the repository tests cover storage; the seam between them is exercised by the manual walkthrough | A test host that wires the real chain against Azurite |
| Parked events are never expired | A shipment whose booking is never confirmed keeps them indefinitely, which is the honest state | A sweep job and a retention rule |
| No metric on parked-event depth | The number is visible in storage, not on a dashboard | A custom metric from the dispatcher |
| The lifecycle table is not rebuildable by a command | It is rebuildable in principle from the snapshots | A maintenance function that walks the container |
| `IsInvoiceSubmitted` has no consumer | Delivery confirmation sets it; nothing reads it yet | A finance client |

Add to this table rather than leaving a gap unwritten — an unlisted gap reads as something nobody
noticed.
