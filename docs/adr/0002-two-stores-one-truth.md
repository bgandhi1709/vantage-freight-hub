# 0002 — A canonical snapshot and a derived index

**Status:** accepted

## Context

Two questions get asked of this data, and they want different shapes:

- *What is everything known about this consignment?* — a whole aggregate, read by reference, and
  the thing a dispute is settled with.
- *What is in flight for this consignee, and what is parked waiting on an account?* — a filtered
  list, read across many consignments.

One store answers both badly. A blob cannot be queried; a table row cannot hold the full command,
the built requests and a list of parked events without becoming unreadable.

## Decision

Both, with an explicit hierarchy: the blob snapshot is canonical, the table row is an index
derived from it and rebuildable from it. Every write goes through `ShipmentStateWriter`, which
writes the snapshot first and the index second.

The order is the point. The index is what other people query; if it were written first, a crash in
between would leave it claiming a state the snapshot cannot confirm — which reads, to anyone
looking, as a shipment that reached a step it never reached. The other way round, the index is
briefly stale, and the next write repairs it.

The status value is written **by name** in both stores. Mixing an ordinal in one with a name in the
other is invisible until somebody inserts an enum member, at which point every previously written
ordinal means something else.

## Consequences

- Two writes per transition. Accepted: they are cheap, and the alternative is one store doing both
  jobs badly.
- A partial failure leaves the index behind the snapshot, never ahead of it.
- Rebuilding the index from snapshots is possible, and is the recovery story if it ever drifts.
