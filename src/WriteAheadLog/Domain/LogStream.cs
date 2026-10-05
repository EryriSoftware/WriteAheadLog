using System.IO.Hashing;
using System.Runtime.CompilerServices;
using Eryri.Buffers;
using Eryri.Buffers.Extensions;
using Eryri.WriteAheadLog.Models;

namespace Eryri.WriteAheadLog.Domain;

internal class LogStream : IDisposable
{
    private const int BufferSize = 64 << 10; // 64 KB
    public const long GuidLength = 16;
    private readonly FileStream fileStream;
    private Guid generation;
    public Checkpoint Checkpoint => new Checkpoint(generation, fileStream.Position);

    public LogStream(string LogPath)
    {
        fileStream = new FileStream(
        LogPath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.Read,
        bufferSize: BufferSize,
        options: FileOptions.Asynchronous);

        if (fileStream.Length >= GuidLength)
        {
            fileStream.Seek(0, SeekOrigin.Begin);
            generation = fileStream.ReadGuid();
            fileStream.Seek(0, SeekOrigin.End);
        }
        else
        {
            Reset();
        }
    }

    public void Dispose() => fileStream.Dispose();

    public Task FlushAsync(CancellationToken cancellationToken) => fileStream.FlushAsync(cancellationToken);
    public void Flush(bool flushToDisk) => fileStream.Flush(flushToDisk);
    public async ValueTask Write(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        fileStream.Write(payload.Length);

        await fileStream.WriteAsync(payload, cancellationToken);

        var checksum = Crc32.HashToUInt32(payload.Span);
        fileStream.Write(checksum);
    }

    public void Reset()
    {
        fileStream.SetLength(0);
        generation = Guid.NewGuid();
        fileStream.Write(generation);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> Read([EnumeratorCancellation] CancellationToken cancellationToken, Checkpoint? checkpoint)
    {
        try
        {
            fileStream.Seek(0, SeekOrigin.Begin);

            if (fileStream.Length >= GuidLength)
            {
                generation = fileStream.ReadGuid();
            }

            if (checkpoint is { } cp && cp.Generation == generation)
            {
                fileStream.Seek(cp.Position, SeekOrigin.Begin);
            }

            while (fileStream.Position + 4 <= fileStream.Length)
            {
                var recordStart = fileStream.Position;
                var length = fileStream.ReadInt();

                if (length > 0 && fileStream.Position + 4 + length <= fileStream.Length)
                {
                    using var buffer = new ArrayPoolBufferWriter<byte>();
                    var memory = buffer.GetMemory(length + 4).Slice(0, length + 4);
                    await fileStream.ReadExactlyAsync(memory, cancellationToken);
                    var payload = memory.Slice(0, length);
                    var checksum = Crc32.HashToUInt32(payload.Span);
                    memory.Span.Slice(length).ReadUInt(out var actualChecksum);

                    if (checksum != actualChecksum)
                    {
                        fileStream.SetLength(recordStart);
                        break;
                    }

                    yield return payload;
                }
                else
                {
                    fileStream.SetLength(recordStart);
                    break;
                }
            }
        }
        finally
        {
            fileStream.Seek(0, SeekOrigin.End);
        }
    }
}
