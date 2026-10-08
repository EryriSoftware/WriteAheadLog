using System.IO.Pipelines;

namespace Eryri.WriteAheadLog.Domain;

internal sealed class WriteOnlyStream : IStreamOwner, IAsyncDisposable, IDisposable
{
    public Stream Stream { get; private init; }
    private readonly Pipe pipe;
    private readonly Task copyTask;
    internal WriteOnlyStream(Stream destinationStream, CancellationToken cancellationToken)
    {
        pipe = new Pipe();
        Stream = pipe.Writer.AsStream();
        copyTask = pipe.Reader.CopyToAsync(destinationStream, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await pipe.Writer.CompleteAsync();
        await copyTask;
        await Stream.DisposeAsync();
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();
}
