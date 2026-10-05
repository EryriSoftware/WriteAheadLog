# Eryri.WriteAheadLog

[![NuGet](https://img.shields.io/nuget/v/Eryri.WriteAheadLog.svg)](https://www.nuget.org/packages/Eryri.WriteAheadLog)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Eryri.WriteAheadLog.svg)](https://www.nuget.org/packages/Eryri.WriteAheadLog)

**Fast, local, restart-safe persistence for .NET application state without introducing a database.**

`Eryri.WriteAheadLog` is a lightweight, high-performance write-ahead log (WAL) for applications that keep state in memory but need that state to survive process restarts.

Records are appended to a local binary log and periodically compacted into a snapshot. On startup, the snapshot is loaded and the remaining WAL is replayed to reconstruct application state.

It is designed for **single-process workloads** where local persistence, predictable performance and simple recovery are more important than database features such as querying, indexing, transactions or replication.

## Why use it?

* **Persist application state locally** without introducing an embedded database.
* **Append strongly typed records** rather than materialising serialized `byte[]` objects at the application boundary.
* **Serialize directly into an ArrayPool-backed buffer** to reduce temporary allocations.
* **Queue writes asynchronously** using `System.Threading.Channels`.
* **Serialize all WAL operations through a single consumer**, providing ordered file access.
* **Choose durability per record** — `Queue`, `Write`, `Flush`, or `Durable`.
* **Detect incomplete or corrupted final records** using CRC32 checksums.
* **Recover automatically after an interrupted final write.**
* **Checkpoint snapshots against the WAL** using a generation identifier and WAL position.
* **Avoid replaying records already represented by a matching snapshot.**
* **Periodically snapshot state** to prevent the WAL from growing indefinitely.
* **Version persisted formats** so incompatible application-state formats can invalidate existing state safely.
* Keep the persistence layer **small and explicit** rather than introducing a general-purpose database.

## Good fit

`Eryri.WriteAheadLog` is a good fit when:

* application state primarily lives in memory;
* the state can be reconstructed by replaying records;
* persistence is local to one process;
* writes are append-oriented;
* the application controls the persisted record format;
* restart recovery is more important than querying persisted data;
* you want minimal infrastructure around local persistence.

Typical applications include:

* in-memory state machines;
* local caches requiring restart recovery;
* background workers;
* local indexes;
* trading or market-data state;
* game/server state;
* local service state;
* write-heavy applications that periodically checkpoint their state.

## Not a fit

This library is deliberately **not** a database.

Use a database or distributed log when you need:

* SQL or arbitrary queries;
* indexes;
* multi-process writers;
* transactions across multiple resources;
* replication;
* distributed consensus;
* consumer offsets;
* durable event-stream semantics;
* long-term archival compatibility;
* arbitrary historical record inspection.

If you need a small append-only persistence mechanism for recovering in-memory state after a restart, this library is designed for that use case.

## Scope at a glance

| Capability                       | Supported |
| -------------------------------- | --------- |
| Strongly typed records           | Yes       |
| Asynchronous append              | Yes       |
| Concurrent callers               | Yes       |
| Ordered WAL writes               | Yes       |
| Single-process                   | Yes       |
| ArrayPool-backed serialization   | Yes       |
| CRC32 record integrity           | Yes       |
| Incomplete final-record recovery | Yes       |
| Snapshots                        | Yes       |
| WAL generation/checkpointing     | Yes       |
| Format versioning                | Yes       |
| Configurable durability          | Yes       |
| Multi-process writers            | No        |
| Replication                      | No        |
| Distributed consensus            | No        |
| Querying/indexing                | No        |
| Transaction engine               | No        |

## Installation

```bash
dotnet add package Eryri.WriteAheadLog
```

Or add the package directly to your project:

```xml
<PackageReference Include="Eryri.WriteAheadLog" Version="1.0.0" />
```

## How it works

The WAL stores a generation identifier followed by a sequence of records using a compact binary format:

```text
┌──────────────────┬────────────┬────────────────┬────────────┐
│ 16-byte          │ 4-byte     │ serialized     │ 4-byte     │
│ generation       │ length     │ payload        │ CRC32      │
└──────────────────┴────────────┴────────────────┴────────────┘
```

The **generation** is a GUID identifying the current WAL generation. It is written once at the beginning of the WAL and changes whenever the WAL is reset.

The length is a little-endian `Int32`.

The payload is produced by the application's `Serialize` implementation.

The CRC32 is calculated over the serialized payload.

Records are appended sequentially to:

```text
state.wal
```

### Recovery

On initialization, the WAL:

1. Loads the latest snapshot, if one exists.
2. Reads the snapshot's WAL checkpoint, consisting of its generation and position.
3. Reads the current WAL generation.
4. If the snapshot generation matches the WAL generation, seeks directly to the snapshot's WAL position.
5. If the generations differ, starts replaying from the beginning of the current WAL's records.
6. Reads each remaining record length.
7. Ensures the complete payload and checksum are present.
8. Validates the CRC32 checksum.
9. Deserializes and applies each valid record.
10. Truncates the WAL at the first incomplete or invalid record.
11. Starts the background consumer.

The generation allows the WAL to distinguish between a WAL that still contains records represented by the snapshot and a new WAL created after a successful snapshot/reset.

For example, a snapshot might contain:

```text
Generation: A
WAL position: 123456
```

If the WAL also begins with generation `A`, recovery can safely seek directly to position `123456`.

If the WAL begins with generation `B`, the WAL has been reset since that snapshot was created, so the checkpoint position must not be reused. Recovery instead starts from the beginning of the new WAL.

For example:

```text
Snapshot:
    Generation A
    Position 123456
    Application state

WAL:
    Generation A
    [records represented by snapshot]
    [new record]
    [new record]
```

Recovery seeks to the checkpoint:

```text
WAL:
    Generation A
    [snapshot records]
                    ↑
              checkpoint
                    │
                    ├── replay new record
                    └── replay new record
```

After a successful snapshot, the WAL is reset and receives a new generation:

```text
Snapshot:
    Generation A
    Position 123456
    Application state

WAL:
    Generation B
    [new records]
```

The generation mismatch tells recovery that the old checkpoint position no longer applies to the current WAL.

An interrupted final record:

```text
[valid record]
[valid record]
[valid record]
[partial record]
```

becomes:

```text
[valid record]
[valid record]
[valid record]
```

This is specifically designed to handle an interrupted final append.

The WAL does **not** attempt to recover arbitrary corruption in the middle of the file. If a record earlier in the log is corrupted, recovery stops at that point and the corrupted tail is discarded.

## Basic usage

A concrete WAL implementation supplies the application-specific serialization, deserialization, state application and snapshot logic.

```csharp
public sealed class MyWal : WriteAheadLog<MyRecord>
{
    protected override ulong FormatVersion => 1;

    public MyWal(string directory)
        : base(directory)
    {
    }

    protected override void Serialize(
        MyRecord record,
        IBufferWriter<byte> buffer)
    {
        buffer.Write(record.Id);
        buffer.Write(record.Value);
    }

    protected override MyRecord Deserialize(
        ReadOnlyMemory<byte> record)
    {
        var span = record.Span;

        var offset = 0;

        offset += span.ReadGuid(out var id);
        offset += span.Slice(offset).ReadInt(out var value);

        return new MyRecord(id, value);
    }

    protected override ValueTask ApplyAsync(
        MyRecord record,
        CancellationToken cancellationToken)
    {
        // Apply the recovered record to application state.
        return ValueTask.CompletedTask;
    }

    protected override ValueTask WriteSnapshotAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        // Write the current application state.
        return ValueTask.CompletedTask;
    }

    protected override ValueTask ReadSnapshotAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        // Restore application state.
        return ValueTask.CompletedTask;
    }
}
```

Initialize the WAL before recovery is required:

```csharp
await using var wal = new MyWal("./data");

await wal.InitializeAsync(cancellationToken);
```

Commands may also be queued before initialization. They remain in the WAL's internal channel and are processed after initialization has completed recovery and started the background consumer.

Records can then be appended:

```csharp
await wal.AppendAsync(
    new MyRecord(...),
    cancellationToken);
```

## Durability

Each append can specify its required durability level.

```csharp
await wal.AppendAsync(
    record,
    Durability.Write,
    cancellationToken);
```

The available levels are:

### `Durability.None`

The record is placed on the WAL's internal queue and `AppendAsync` returns without waiting for the record to reach the file.

This provides the lowest caller latency and highest potential throughput.

Because the channel is unbounded, sustained production faster than the WAL can consume will increase memory usage.

### `Durability.Write`

The call waits until the record has been written to the WAL stream.

```csharp
await wal.AppendAsync(
    record,
    Durability.Write,
    cancellationToken);
```

This establishes that the WAL consumer has completed the file write.

It does **not** by itself guarantee that the data has reached stable physical storage.

### `Durability.Flush`

The call waits for the record to be written and the underlying `FileStream` to be flushed.

```csharp
await wal.AppendAsync(
    record,
    Durability.Flush,
    cancellationToken);
```

Use this when stronger persistence semantics are required.

The exact guarantees of a flush depend on the operating system, filesystem and storage hardware. A successful `FlushAsync` should not be interpreted as an absolute guarantee against every form of hardware or filesystem failure.

### `Durability.Durable`

The call waits for the record to be written and calls `FileStream.Flush(true)`.

```csharp
await wal.AppendAsync(
    record,
    Durability.Durable,
    cancellationToken);
```

This is the strongest durability level provided by the WAL and explicitly requests that the underlying storage be flushed.

As with all storage systems, the exact guarantees ultimately depend on the operating system, filesystem and storage hardware.

## Explicit flushing

Records appended with `Durability.None` or `Durability.Write` can be flushed later:

```csharp
await wal.FlushAsync(cancellationToken);
```

This is useful when an application wants to batch multiple writes and establish a persistence boundary afterwards.

For example:

```csharp
await wal.AppendAsync(record1, Durability.None, cancellationToken);
await wal.AppendAsync(record2, Durability.None, cancellationToken);
await wal.AppendAsync(record3, Durability.None, cancellationToken);

await wal.FlushAsync(cancellationToken);
```

To request a durable storage flush:

```csharp
await wal.FlushAsync(
    cancellationToken,
    flushToDisk: true);
```

## Snapshots

Snapshots capture the application's current state and allow the WAL to be compacted.

```csharp
await wal.SaveSnapshotAsync(cancellationToken);
```

Each snapshot records a checkpoint containing:

* the WAL generation;
* the WAL position represented by the snapshot;
* the application-specific snapshot state.

The snapshot is first written to a temporary file:

```text
state.snapshot.tmp
```

The temporary snapshot is flushed to disk before replacing the existing snapshot.

After the new snapshot has been successfully installed, the WAL is reset with a **new generation**.

This ordering allows recovery to safely handle crashes during snapshot creation.

For example:

```text
1. Write snapshot.tmp
2. Flush snapshot.tmp to disk
3. Replace state.snapshot
4. Reset WAL
5. Generate new WAL generation
```

If the process fails before the snapshot is installed, the existing snapshot and WAL remain available.

If the snapshot has been installed but the WAL has not yet been reset, the snapshot's generation and position allow recovery to skip records already represented by the snapshot.

If the WAL has been reset, its new generation prevents an old snapshot position from being incorrectly applied to the new WAL.

### Snapshot responsibility

The WAL does not know how to serialize application state.

The implementation provides:

```csharp
protected abstract ValueTask WriteSnapshotAsync(
    Stream stream,
    CancellationToken cancellationToken);

protected abstract ValueTask ReadSnapshotAsync(
    Stream stream,
    CancellationToken cancellationToken);
```

This allows each application to choose its own snapshot representation.

## Format versioning

Each WAL directory contains:

```text
state.version
```

The stored version is an unsigned 64-bit integer.

Implementations specify their current persisted format using:

```csharp
protected override ulong FormatVersion => 1;
```

If the stored version differs from the current implementation's `FormatVersion`, the existing WAL and snapshot are discarded and a new state is created.

For example:

```csharp
protected override ulong FormatVersion => 2;
```

This is intentional.

`Eryri.WriteAheadLog` is designed for **application state recovery**, not long-term archival compatibility. If the serialized representation changes incompatibly, increment the format version rather than attempting to deserialize data written by an incompatible implementation.

## Storage layout

A WAL directory normally contains:

```text
<data directory>/
├── state.version
├── state.wal
└── state.snapshot
```

`state.wal` contains a generation identifier followed by WAL records.

`state.snapshot` contains the snapshot checkpoint and application state.

During snapshot creation:

```text
<data directory>/
├── state.version
├── state.wal
├── state.snapshot
└── state.snapshot.tmp
```

The storage directory is created automatically if it does not already exist.

## Threading model

Multiple application threads can append concurrently:

```text
Writer A ─┐
Writer B ─┼──► Channel ──► WAL consumer ──► state.wal
Writer C ─┘
```

The WAL uses an unbounded `System.Threading.Channels.Channel` with:

* multiple writers;
* a single reader;
* asynchronous processing;
* synchronous continuations disabled.

Only the single WAL consumer interacts with the WAL stream.

This means callers do not need to coordinate file access with one another.

### Ordering

The WAL establishes persistence ordering through the single consumer.

The order in which commands are accepted by the channel determines their processing order.

Applications that require their own stronger ordering guarantees should establish that ordering before calling `AppendAsync`.

## Performance model

The WAL separates application threads from the physical file writer:

```text
Multiple application threads
            │
            ▼
      Channel<Command>
            │
            ▼
        WAL consumer
            │
            ▼
  ArrayPool-backed buffer
            │
            ▼
         FileStream
            │
            ▼
        state.wal
```

Each record remains as its original `T` value while waiting in the channel rather than being eagerly serialized into a separate `byte[]`.

When the consumer processes an append:

1. the record is serialized into an `ArrayPool`-backed buffer;
2. the payload length is written;
3. the CRC32 is calculated;
4. the checksum is appended;
5. the complete record is written to the WAL stream.

This avoids requiring every caller to allocate and retain a separately serialized payload while waiting for persistence.

The serialization buffer grows automatically for larger records and uses pooled storage to reduce managed-array allocations.

## Serialization

Serialization is intentionally application-defined:

```csharp
protected abstract void Serialize(
    T record,
    IBufferWriter<byte> buffer);
```

Deserialization receives the serialized payload:

```csharp
protected abstract T Deserialize(
    ReadOnlyMemory<byte> record);
```

This keeps the WAL independent of a particular serialization framework.

For high-performance binary formats, `IBufferWriter<byte>` can be used directly with `Eryri.Buffers`:

```csharp
protected override void Serialize(
    MyRecord record,
    IBufferWriter<byte> buffer)
{
    buffer.Write(record.Id);
    buffer.Write(record.Value);
}
```

This allows the WAL to write directly into its pooled serialization buffer without first constructing an intermediate `byte[]`.

## Lifecycle

The intended lifecycle is:

```text
Create
  │
  ├── Queue commands
  │
  ▼
InitializeAsync()
  │
  ├── Load snapshot
  ├── Validate snapshot checkpoint
  ├── Replay WAL from checkpoint
  ├── Recover incomplete tail
  └── Start consumer
  │
  ▼
Process queued commands
  │
  ▼
Append / Flush / Snapshot
  │
  ▼
DisposeAsync()
```

`InitializeAsync` performs recovery before the background consumer starts. This ensures that recovered application state is established before queued WAL commands are processed.

Commands may be queued before initialization. They are not processed until the recovery phase has completed and the background consumer has started.

## Disposal

The WAL supports both synchronous and asynchronous disposal.

For asynchronous applications:

```csharp
await wal.DisposeAsync();
```

For synchronous applications:

```csharp
wal.Dispose();
```

Asynchronous disposal flushes pending WAL work durably before the background consumer and underlying resources are released.

`DisposeAsync` is recommended for applications already using asynchronous startup and shutdown.

## Error handling

The WAL is intentionally conservative during recovery.

An incomplete record at the end of the WAL is treated as an interrupted append and the file is truncated back to the beginning of that record.

A CRC32 mismatch causes the WAL to truncate at the beginning of the invalid record, discarding that record and everything after it.

The WAL does not attempt to reconstruct corrupted records or repair arbitrary corruption in the middle of the file.

Applications requiring stronger protection against storage corruption should use an appropriate database or storage system with the required durability and recovery guarantees.

## Design goals

The project intentionally focuses on a narrow problem:

> **Fast, local, restart-safe persistence of application state.**

It prioritises:

* low allocation overhead;
* asynchronous writes;
* ordered append processing;
* explicit binary serialization;
* predictable recovery;
* generation-based WAL checkpointing;
* simple storage;
* application-controlled persistence formats.

It deliberately avoids becoming:

* a relational database;
* a document database;
* a distributed consensus log;
* a replicated WAL;
* a transactional database engine;
* a general-purpose event store;
* a replacement for SQLite or another embedded database.

If you need querying, indexing, transactions, replication or multi-process coordination, a database is likely a better choice.

If you need a small append-only persistence mechanism for recovering in-memory state after a restart, `Eryri.WriteAheadLog` is designed for that use case.
