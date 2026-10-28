---
id: winmanager-contracts
type: module-design
status: draft
title: Public contracts and serialization
parent: winmanager-architecture
---

# Public contracts and serialization

## Responsibility

Contracts defines shared commands, desktop observations, layout trees, workspace snapshots, events, and protocol envelopes.
It also owns pipe framing, public pipe naming, and source-generated protocol JSON metadata.
`winmanager-protocol` defines the wire format and client consistency contract.

## Boundary

This project has no production project dependency. It contains no engine state, window-control policy, or generated Win32 bindings.
Most records are platform-neutral. Default pipe naming uses the Windows user SID and session, so the assembly is not wholly platform-independent.

Public window, workspace, monitor, and node IDs are GUIDs. They do not expose native handles or promise identity across host restarts.
Protocol version 1 includes additive commands without capability negotiation. New clients cannot assume older hosts support all current fields.

Source-generated JSON covers protocol types. It does not prove AOT compatibility for host configuration, recovery persistence, or CLI output.
