---
id: winmanager-client
type: module-design
status: draft
title: External .NET client
parent: winmanager-architecture
depends-on: [winmanager-contracts]
references: [winmanager-protocol]
---

# External .NET client

## Responsibility and boundary

Client supplies typed queries, commands, and asynchronous event subscriptions over public IPC.
It references Contracts only. It cannot inspect engine internals or invoke the native adapter.
`winmanager-protocol` owns framing and ordering. The SDK validates envelopes and full snapshots before delivery.

## Client behavior

One reader demultiplexes responses and events. Requests correlate by ID rather than arrival order.
Each connection supports one active subscription and each subscription supports one event consumer.
The event queue is bounded. Consumer lag terminates the connection rather than silently discarding events.

Request cancellation aborts the shared connection and its pending operations. It does not prove that an admitted command caused no effects.
`WatchAsync` can reconnect after transport failures with a fresh subscription and snapshot. It never retries commands or replays missed transitions.
Protocol errors and consumer lag are fatal rather than reconnectable failures.

The library targets `net10.0`, but live pipe connections require Windows. GUIDs from a previous host instance are not restart-matching identities.
