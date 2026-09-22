# 0004 — Narrow identifier rules, and one that was wrong

**Status:** accepted

## Context

Three systems name the same consignment three ways: the booking system's reference, the forwarder's
own reference (all its callbacks carry only this), and a padded order number. A retried booking
arrives with an attempt marker appended, and must land on the same storage key as the original —
otherwise the forwarder receives two consignments for one booking.

## Decision

Each rule is as narrow as it can be, and each has its own tests.

- **Retry marker.** Only a trailing `-r<N>` is stripped. Nothing else.
- **Order number.** Padding is stripped only from an exact all-numeric three-part shape. A
  partner-prefixed number passes through untouched: the padding is meaningful to whoever issued it.
- **Storage keys.** Every key goes through one function that normalizes and lower-cases, because
  blob and table storage both compare keys as case-sensitive strings.

## The rule that was wrong

The first version of the retry rule stripped *any* short trailing number from a reference with
three or more dash-separated parts. It looked reasonable and had passing unit tests, because those
tests only used four-part inputs.

It is unsound. `VW-1042-7` is an ordinary reference whose last segment happens to be a small
number, and the rule rewrote it to `VW-1042` — merging two unrelated consignments onto one storage
key. The repository tests caught it: a snapshot saved under one reference could not be read back
under the same reference.

The fix was to stop guessing. An explicit marker the booking system emits removes the ambiguity
entirely, and the tests now pin the cases that must be left alone as firmly as the ones that are
rewritten.

## Consequences

- The booking system must emit `-r<N>`. That is a contract, and it is written down here.
- The test file reads as the specification of the identity model, which is the intent.
- A heuristic that infers intent from the shape of an identifier is a bug waiting for the right
  input. This one waited about an hour.
