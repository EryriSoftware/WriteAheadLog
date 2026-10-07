using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Channels;
using Eryri.Buffers;
using Eryri.Buffers.Extensions;
using Eryri.WriteAheadLog.Domain;
using Eryri.WriteAheadLog.Extensions;
using Eryri.WriteAheadLog.Models;

namespace Eryri.WriteAheadLog;

public abstract class WriteAheadLog<T> : IWriteAheadLog<T>, IDisposable, IAsyncDisposable
{
    protected abstract ulong FormatVersion { get; }
    protected bool IsDisposed { get; private set; }
    private bool initialized;
    private bool hasChanges = false;
    private const int BufferSize = 64 << 10; // 64 KB
    private readonly string directory;
    private string LogPath => Path.Combine(directory, "state.wal");
    private string SnapshotPath => Path.Combine(directory, "state.snapshot");
    private string VersionPath => Path.Combine(directory, "state.version");
    private readonly LogStream logStream;
    private readonly SemaphoreSlim sync = new (1, 1);
    private readonly CancellationTokenSource ctSource = new CancellationTokenSource();
    private Task? consumer;
    private readonly Channel<Command> channel = Channel
        .CreateUnbounded<Command>(
    new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    protected WriteAheadLog(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        CheckVersion();

        logStream = new LogStream(LogPath);
    }

    private void CheckVersion()
    {
        using var fs = new FileStream(
            VersionPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: BufferSize);

        if (fs.Length != 8 || fs.ReadULong() != FormatVersion)
        {
            Delete(LogPath);
            Delete(SnapshotPath);
        }

        fs.Seek(0, SeekOrigin.Begin);
        fs.SetLength(0);
        fs.Write(FormatVersion);
        fs.Flush(true);

        void Delete(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private async Task ConsumerLoop(CancellationToken cancellationToken)
    {
        await foreach (var command in channel.Reader.ReadAllAsync(cancellationToken))
        {
            await sync.WaitAsync(cancellationToken);

            try
            {
                var task = command switch
                {
                    AppendCommand<T> append => HandleCommand(append, cancellationToken),
                    FlushCommand flush => HandleCommand(flush, cancellationToken),
                    SaveCommand save => HandleCommand(save, cancellationToken),
                    _ => throw new NotImplementedException(command.GetType().Name)
                };
                await task;
                command.Complete();
            }
            catch (Exception ex)
            {
                command.Fail(ex);
            }
            finally
            {
                sync.Release();
            }
        }
    }

    private async ValueTask HandleCommand(AppendCommand<T> log, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref hasChanges, true);
        using var buffer = new ArrayPoolBufferWriter<byte>();

        Serialize(log.Value, buffer);
        await logStream.Write(buffer.WrittenMemory, cancellationToken);

        if (log.durability == Durability.Flush)
        {
            await logStream.FlushAsync(cancellationToken);
        }
        else if (log.durability == Durability.Durable)
        {
            logStream.Flush(true);
        }
    }

    private async ValueTask HandleCommand(FlushCommand flush, CancellationToken cancellationToken)
    {
        if (flush.flushToDisk)
        {
            logStream.Flush(flush.flushToDisk);
        }
        else
        {
            await logStream.FlushAsync(cancellationToken);
        }
    }

    private async ValueTask HandleCommand(SaveCommand command, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref hasChanges))
        {
            var tempPath = SnapshotPath + ".tmp";

            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: BufferSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                stream.Write(logStream.Checkpoint);
                await using (var readStream = stream.AsWriteOnly(cancellationToken))
                {
                    await WriteSnapshotAsync(readStream.Stream, cancellationToken);
                }

                stream.Flush(true);
            }

            File.Move(tempPath, SnapshotPath, overwrite: true);
            logStream.Reset();
            Interlocked.Exchange(ref hasChanges, false);
        }
    }

    public virtual async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await sync.WaitAsync(cancellationToken);

        try
        {
            if (initialized)
            {
                return;
            }

            Checkpoint? checkpoint = null;
            if (File.Exists(SnapshotPath))
            {
                await using var stream = new FileStream(
                SnapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: BufferSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                checkpoint = stream.ReadCheckpoint();
                await using (var readStream = stream.AsReadOnly(cancellationToken))
                {
                    await ReadSnapshotAsync(readStream.Stream, cancellationToken);
                }
            }

            await foreach (var item in logStream.Read(cancellationToken, checkpoint))
            {
                var record = Deserialize(item);
                await ApplyAsync(record, cancellationToken);
            }

            consumer = ConsumerLoop(ctSource.Token);
            initialized = true;
        }
        finally
        {
            sync.Release();
        }
    }

    public ValueTask AppendAsync(T record, CancellationToken cancellationToken) => AppendAsync(record, Durability.Queue, cancellationToken);
    public async ValueTask AppendAsync(T record, Durability durability, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var command = new AppendCommand<T>(record, durability);
        await channel.Writer.WriteAsync(command, cancellationToken);

        if (durability != Durability.Queue)
        {
            await command.Task.WaitAsync(cancellationToken);
        }
    }

    protected abstract void Serialize(T record, IBufferWriter<byte> buffer);
    protected abstract T Deserialize(ReadOnlyMemory<byte> record);
    protected abstract ValueTask ApplyAsync(T record, CancellationToken cancellationToken);
    protected abstract ValueTask WriteSnapshotAsync(Stream stream, CancellationToken cancellationToken);
    protected abstract ValueTask ReadSnapshotAsync(Stream stream, CancellationToken cancellationToken);

    public async Task SaveSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new SaveCommand();
        await channel.Writer.WriteAsync(command, cancellationToken);
        await command.Task.WaitAsync(cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken, bool flushToDisk = false)
    {
        ThrowIfDisposed();
        var command = new FlushCommand(flushToDisk);
        await channel.Writer.WriteAsync(command, cancellationToken);
        await command.Task.WaitAsync(cancellationToken);
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (!IsDisposed)
        {
            if (initialized)
            {
                await FlushAsync(ctSource.Token, true);
                channel.Writer.TryComplete();
                await (consumer ?? Task.CompletedTask);
            }

            ctSource.Cancel();
            ctSource.Dispose();
            logStream.Dispose();
            sync.Dispose();
            IsDisposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool isDisposing)
    {
        if (isDisposing)
        {
            DisposeAsync().GetAwaiter().GetResult();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }
}
