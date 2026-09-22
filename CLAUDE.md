# CLAUDE.md

Working rules for coding agents in this repository. `AGENTS.md` carries the longer reference — the
lifecycle in detail, the storage layout, deployment, and what is knowingly unfinished.

## Working model

1. **Name the layer** the problem belongs to: trigger, dispatcher, handler, repository,
   infrastructure client, or deployment.
2. **Read the code that runs**, plus its configuration. Evidence before assumption.
3. **Follow the whole path**: bus or dev trigger → envelope → dispatcher → guard → handler →
   snapshot and lifecycle row → forwarder call → callback.
4. **Decide how you will prove it** before you write anything.
5. **Ask first** when a change touches any of these:
   - handler registration order, or a guard's condition
   - the park-and-replay path
   - the shape of the snapshot, the lifecycle row, or a storage key
   - the failure policy: what throws, what completes, what is retried
   - an identifier normalization rule
   - a dependency, a deployment, anything destructive

   Everything else that is small and reversible, just do.
6. **Prove it.** A green build says the code compiles, not that the lifecycle still works.
7. **Write down** what changed: this file, `AGENTS.md`, or an ADR, in the same change.

## Commands

```bash
dotnet build VantageFreightHub.slnx -c Debug
dotnet test --solution VantageFreightHub.slnx -c Debug     # needs `npm i -g azurite` on PATH

# Run it locally (needs azure-functions-core-tools@4 and azurite)
azurite --location /tmp/azurite-freight --skipApiVersionCheck &
cd apps/freight-hub && func start

az bicep build --file infra/functions/main.bicep --stdout > /dev/null
```

## Layering rules

- Triggers deserialize, wrap and dispatch. No decisions, no storage, no error handling beyond what
  the platform needs.
- Every decision is a guard. If a handler starts with an `if` that inspects the snapshot, that
  condition belongs in `CanHandle`.
- Repositories persist; they do not orchestrate. A repository that calls a service is the smell.
- The forwarder is reached through its typed client only, and that client goes through
  `IHttpClientFactory` with a retry policy. A second client without one is not acceptable.
- State is written in one place, `ShipmentStateWriter`: snapshot first, lifecycle row second.

## Invariants

- Registration order in `Program.cs` is handler priority. The delivery-submission and
  deferred-delivery guards are exact inverses and must stay that way; a test enforces it.
- An unknown operation type, and an event no handler claims, are logged and completed — never
  thrown. Failing on someone else's event dead-letters a good message.
- A handler that cannot act yet parks the event. The dispatcher drains it. A handler never calls
  the dispatcher back.
- Both parked queues are cleared before anything is replayed.
- Every storage key goes through `ReferenceNormalizer`. Blob and table keys are case-sensitive.
- Enum members are appended, never reordered, and are written by name in both stores.
- A handler that fails rethrows, so the bus's own retry and dead-letter policy applies. Do not
  write a second one in code.
- The forwarder token never appears in a log. `RenderCurl` redacts it and a test proves it.
- Configuration is validated at start-up. A worker missing a setting must not start.

## Validation

| Changed | Run at least | Also run when |
| --- | --- | --- |
| A guard or a handler | `dotnet test --filter` on that area, then the full suite | the registration order or the parked-event path moves |
| A repository | the Azurite-backed tests | a key, a prefix or an entity shape changes |
| The forwarder client | its tests, including the redaction one | a header, a route or the retry policy changes |
| A trigger or `Program.cs` | full suite, then `func start` and the README walkthrough | DI registration or a binding changes |
| Bicep | `az bicep build` on the template *and* every parameter file | a parameter is added or renamed |

Report failures honestly: quote the command, say whether it was already failing before your change,
and list what passed.
