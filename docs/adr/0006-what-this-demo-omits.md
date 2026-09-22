# 0006 — What this project deliberately leaves out

**Status:** accepted

Omissions people would otherwise have to guess about. Each is a decision, not an oversight.

| Left out | Why | What it would take |
| --- | --- | --- |
| A second forwarder | One shows the callback-driven shape; a second only shows it twice | A second typed client and a routing rule on the snapshot |
| Scheduled background work | Nothing here is time-driven; the lifecycle is entirely event-driven | A scheduler and a durable job store |
| A Service Bus emulator in the local loop | The official emulator's platform support is uneven, and the dev HTTP trigger drives the identical dispatcher | An emulator container plus a compose file, if it runs on your platform |
| A deploy pipeline | The Bicep and the CI workflow are here and compile; deploying needs a subscription | A workflow that publishes the package and runs `az deployment group create` |
| Service Bus topology in IaC | Queues, topics and subscriptions are pre-provisioned; the template deploys the app | Bicep for the namespace and its entities |
| Dead-letter handling | Service Bus already retries and dead-letters; a second mechanism in code would just be one more thing to maintain | Nothing — deliberately |
| Invoicing | Delivery confirmation sets the invoiceable flag; nothing downstream consumes it | A finance client and one more callback family |

Two things that are *not* omissions, because they are the point: routing by guard
([0001](0001-guards-not-a-switch.md)) and park-and-replay ([0003](0003-park-and-replay.md)).
