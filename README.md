# Vantage Works — freight hub

An integration service for exhibition freight, built around the fact that the other side answers
late.

Vantage Works ships stands, rigging and print panels to venues. A booking system publishes
shipment events; this service registers each consignment with a freight forwarder and tracks a
lifecycle the forwarder confirms **out of band** — account, booking, pickup, delivery. Those
confirmations arrive minutes or hours later, out of order, and occasionally before this service has
heard of the shipment they refer to. None of that is an error case; it is the normal case, and it
is what the design is for.

The sibling service, [`vantage-creative-approval`](https://github.com/bgandhi1709/vantage-creative-approval),
handles the same company's creative sign-off. The two share a brand and nothing else.

---

## Run it

Requires the .NET 10 SDK, Node 22 (for the emulator and the Functions tools), and:

```bash
npm install -g azurite azure-functions-core-tools@4
```

Start the storage emulator and the host:

```bash
azurite --location /tmp/azurite-freight --skipApiVersionCheck &
cd apps/freight-hub && func start
```

In production the entry points are Service Bus triggers. Locally there is no bus, so a dev-only
HTTP trigger feeds the identical dispatcher — the whole lifecycle is drivable with curl, including
the out-of-order case that is awkward to reproduce on a real bus:

```bash
B=http://localhost:7071/api/dev/events
K=local-development-dev-endpoint-key
post() { curl -s -o /dev/null -w "%{http_code}\n" -X POST "$B?eventType=$1" \
  -H "x-api-key: $K" -H 'Content-Type: application/json' -d "$2"; }

# 1. A booking arrives. The consignee has no account with the forwarder yet, so the shipment
#    parks at AccountCreationPending and the account is requested.
post ShipmentCommand '{"operationType":"SubmitShipment","bookingReference":"VW-2001-9",
  "consigneeId":"acme-expo","consigneeName":"Acme Expo","venue":"Hall 3","pieces":4,
  "weightKg":118.5,"sourceSystem":"bookings"}'

# 2. A driver marks it delivered — before the booking has even been confirmed. This is the event
#    that would be dead-lettered by a naive implementation. It is parked instead.
post ShipmentCommand '{"operationType":"UpdateShipmentStatus","bookingReference":"VW-2001-9",
  "status":"Delivered","sourceSystem":"bookings"}'

# 3. The forwarder confirms the account, which releases the booking.
post AccountCallback '{"eventType":"account","externalId":"FWD-88421","isError":false}'

# 4. The forwarder confirms the booking — and the parked delivery is replayed.
post BookingCallback '{"eventType":"booking","externalId":"FWD-88421","isError":"false"}'

post PickupCallback   '{"eventType":"pickup","externalId":"FWD-88421","isError":false}'
post DeliveryCallback '{"eventType":"delivery","externalId":"FWD-88421","isError":false}'
```

The log reads:

```
Shipment VW-2001-9 moved to AccountCreationPending
Shipment VW-2001-9 moved to AccountCreated
Shipment VW-2001-9 moved to BookingSubmitted
Shipment VW-2001-9 moved to BookingConfirmed
Replaying 1 parked event(s) for VW-2001-9
Shipment VW-2001-9 moved to DeliverySubmitted
Shipment VW-2001-9 moved to PickupConfirmed
Shipment VW-2001-9 moved to DeliveryConfirmed
```

Tests (the storage ones start a real Azurite process of their own):

```bash
dotnet test --solution VantageFreightHub.slnx
```

---

## How it fits together

```
 booking system ──▶ Service Bus queue ──┐
                                        ├──▶ dispatcher ──▶ state handlers ──▶ forwarder API
 forwarder ──▶ Service Bus topic ───────┘         │        callback handlers
   (account, booking, pickup, delivery)           │
                                                  ├──▶ blob: snapshot, index, parked events
                                                  └──▶ table: queryable lifecycle index
```

| Path | What lives there |
| --- | --- |
| `Functions/` | Triggers. Deserialize, wrap, dispatch — no decisions |
| `Services/` | The dispatcher, the state handlers and the callback handlers |
| `Infrastructure/` | The forwarder's typed HTTP client |
| `Repository/` | Blob snapshots and the lifecycle table |
| `Models/` | Commands, callbacks, the snapshot aggregate |
| `infra/functions/` | Bicep for the Function App |
| `docs/adr/` | Why the load-bearing decisions are the way they are |

## Worth reading first

- `Services/ShipmentWorkflowDispatcher.cs` — routing by guard rather than by switch, and the replay
  that drains parked events. ([ADR 0001](docs/adr/0001-guards-not-a-switch.md),
  [ADR 0003](docs/adr/0003-park-and-replay.md))
- `Services/Handlers/DeferredDeliveryHandler.cs` and `DeliverySubmissionHandler.cs` — two guards
  that are exact inverses, with a test that keeps them that way.
- `Services/ShipmentStateWriter.cs` — snapshot first, index second, in one place.
  ([ADR 0002](docs/adr/0002-two-stores-one-truth.md))
- `Services/ReferenceNormalizer.cs` — three systems, three naming conventions, and a rule that was
  wrong until a test caught it. ([ADR 0004](docs/adr/0004-identifier-rules.md))
- `Infrastructure/ForwarderClient.cs` — every outbound call logged as a replayable curl with the
  token redacted. ([ADR 0005](docs/adr/0005-debugging-a-black-box.md))

## What it does not do

Listed in [ADR 0006](docs/adr/0006-what-this-demo-omits.md): one forwarder, no scheduled work, no
deploy pipeline beyond the templates, and no Service Bus emulator in the local loop.

## Licence

MIT. See [LICENSE](LICENSE).
