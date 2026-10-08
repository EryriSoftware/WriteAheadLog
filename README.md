# [Eryri.WriteAheadLog](https://www.nuget.org/packages/Eryri.WriteAheadLog)

[![NuGet](https://img.shields.io/nuget/v/Eryri.WriteAheadLog.svg)](https://www.nuget.org/packages/Eryri.WriteAheadLog)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Eryri.WriteAheadLog.svg)](https://www.nuget.org/packages/Eryri.WriteAheadLog)

**A high-throughput, replayable, append-only log for .NET.**

`Eryri.WriteAheadLog` is a lightweight local persistence engine for applications that need fast ordered writes, deterministic replay, and optional snapshot-based compaction.

Write records to a durable binary log, replay them from the beginning or from a checkpoint, and optionally create snapshots to make recovery fast.

It can be used as a **write-ahead log, local event store, event-sourcing foundation, replayable event log, or high-performance application persistence layer** — without requiring a database.

## Why use it?

* **High-throughput append-only writes** through an asynchronous single-writer pipeline.
* **Replay indefinitely** from the beginning of the log to reconstruct application state.
* **Optional snapshots** to compact history and accelerate recovery.
* **Application-defined serialization** using `IBufferWriter<byte>`.
* **Configurable durability** from queued writes through to explicitly flushed and durable records.
* **CRC32 integrity checks** detect incomplete or corrupted records.
* **Generation-based checkpoints** prevent stale snapshot offsets being applied to a new log.
* **Binary format versioning** allows incompatible persisted state to be discarded safely.
* **No database required** — persistence is entirely local to the application.

## The core idea

The WAL is the history.

A snapshot is simply an optimisation for replay.

```text
     Append
       │
       ▼
┌──────────────┐
│      WAL     │
│              │
│ Event 1      │
│ Event 2      │
│ Event 3      │
│ ...          │
│ Event N      │
└──────┬───────┘
       │
    Replay
       │
       ▼
Application State
```

When snapshots are enabled:

```text
┌──────────────┐
│   Snapshot   │
│   at Event N │
└──────┬───────┘
       │
       ▼
Replay Event N+1
       │
       ▼
Current State
```

This means the application can choose the right trade-off between **history, recovery time, and storage**.

## Event sourcing

The library is deliberately well suited to event sourcing.

An application can treat each WAL record as an event and rebuild its state entirely by replaying the log:

```text
Command
   │
   ▼
Append Event
   │
   ▼
┌──────────────┐
│ Event Store  │
│    (WAL)     │
└──────┬───────┘
       │
       ▼
     Replay
       │
       ▼
   Projection
```

Snapshots can then be introduced purely as a performance optimisation when replaying the complete history becomes unnecessary.

`Eryri.WriteAheadLog` does not impose an event-sourcing architecture. It provides the underlying **ordered, persistent, replayable log** that such an architecture can be built upon.

## When it fits

### Excellent fit

* Event-sourced applications
* Local event stores
* In-memory state with persistent recovery
* Stateful background services
* Trading and market-data systems
* Local indexes and caches
* Game/server state
* High-throughput local persistence
* Applications requiring deterministic replay
* Systems where a database would add unnecessary operational complexity

### Not a database

This library intentionally does not provide:

* SQL queries
* Secondary indexes
* Distributed replication
* Multi-process coordination
* Distributed transactions
* Consensus
* Network access

If you need those capabilities, use a database or distributed log.

`Eryri.WriteAheadLog` is the **local persistence primitive**, not a replacement for every kind of data store.

## Install

```bash
dotnet add package Eryri.WriteAheadLog
```

## Getting started

Create a concrete WAL by implementing four application-specific operations:

```csharp
public sealed class MyWal : WriteAheadLog<MyEvent>
{
    protected override ulong FormatVersion => 1;

    protected override void Serialize(
        MyEvent value,
        IBufferWriter<byte> writer)
    {
        // Serialize value to writer.
    }

    protected override MyEvent Deserialize(
        ReadOnlyMemory<byte> data)
    {
        // Deserialize value.
    }

    protected override ValueTask ApplyAsync(
        MyEvent value,
        CancellationToken cancellationToken)
    {
        // Apply the event to application state.
    }

    protected override Task WriteSnapshotAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        // Serialize application state.
    }

    protected override Task ReadSnapshotAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        // Restore application state.
    }
}
```

Then:

```csharp
await using var wal = new MyWal("state");

await wal.InitializeAsync();

await wal.AppendAsync(
    new MyEvent(...),
    Durability.Durable);
```

The application owns the serialization and state model. The WAL owns the persistence, ordering, integrity, replay and recovery mechanics.

## Durability

Writes can be assigned different durability requirements:

```csharp
await wal.AppendAsync(value, Durability.Queue);
await wal.AppendAsync(value, Durability.Write);
await wal.AppendAsync(value, Durability.Flush);
await wal.AppendAsync(value, Durability.Durable);
```

This allows applications to choose the appropriate balance between **latency, throughput and durability**.

For example:

* `Queue` — enqueue the record and return immediately.
* `Write` — wait for the record to be written.
* `Flush` — wait for the stream to be flushed.
* `Durable` — flush the underlying file to stable storage.

The WAL preserves record ordering regardless of the durability mode.

## Replay and recovery

On startup, the WAL can:

1. Restore the latest snapshot, if present.
2. Validate its checkpoint against the WAL generation.
3. Seek to the checkpoint position when valid.
4. Replay subsequent records.
5. Reconstruct the current application state.

Without a snapshot, the complete WAL can simply be replayed from the beginning.

This makes the same log suitable for both **fast recovery** and **complete historical replay**.

## Snapshots

Snapshots are optional.

They allow an application to periodically persist its current state and discard WAL history that is no longer required for recovery.

```text
WAL

Event 1
Event 2
Event 3
Event 4
Event 5
   ▲
   │
Snapshot checkpoint
   │
Event 6
Event 7
Event 8
```

After a successful snapshot, recovery only needs to restore the snapshot and replay events after its checkpoint.

Snapshots therefore improve recovery time without changing the underlying event history model.

## Data integrity

Each WAL record contains:

```text
┌────────────┬──────────────┬────────────┐
│   Length   │   Payload    │    CRC32   │
└────────────┴──────────────┴────────────┘
```

Records are validated during replay so incomplete or corrupted records cannot silently become application state.

The WAL also maintains a **generation identifier**. Snapshots store the generation alongside their checkpoint position, ensuring a checkpoint from an older WAL cannot accidentally be applied to a newly-created log.

## Format versioning

Implementations define their persisted format version:

```csharp
protected override ulong FormatVersion => 1;
```

When the stored format version differs from the implementation's current version, the existing WAL and snapshot are discarded and a new state is created.

This provides an explicit and deterministic mechanism for handling incompatible persisted formats.

## Performance-oriented design

The library is designed around a simple principle:

> **Keep the hot path append-only and let the application control the representation of its data.**

Serialization writes directly into an `IBufferWriter<byte>`, avoiding unnecessary intermediate allocations.

Writes are processed through an ordered asynchronous pipeline, allowing producers to enqueue work without each producer independently coordinating access to the underlying file.

Snapshots stream directly to disk rather than requiring the complete snapshot to exist in memory.

The result is a small persistence primitive that can handle workloads where **throughput and predictable local I/O matter more than database features**.

## What makes it different?

Most persistence abstractions answer:

> "How do I store this state?"

`Eryri.WriteAheadLog` answers a different question:

> **"How do I reliably record this history and replay it later?"**

That distinction makes it useful far beyond traditional WAL scenarios.

The same primitive can underpin:

* application state recovery
* event sourcing
* event replay
* local event stores
* materialized state
* snapshots
* deterministic reconstruction
* append-only persistence

## Design philosophy

`Eryri.WriteAheadLog` deliberately leaves application semantics to the application.

It does not dictate:

* your event types
* your serialization format
* your state model
* your event-sourcing architecture
* your snapshot representation
* your retention strategy

It provides the persistence primitive underneath them.

**Append. Replay. Recover.**

That's it.
