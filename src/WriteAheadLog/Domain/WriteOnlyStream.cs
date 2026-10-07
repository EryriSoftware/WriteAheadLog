using System.IO.Pipelines;

namespace Eryri.WriteAheadLog.Domain;

public sealed class WriteOnlyStream : IStreamOwner, IAsyncDisposable, IDisposable
{
    public Stream Stream { get; private init; }
    private readonly Pipe pipe;
    private readonly Task copyTask;
    public WriteOnlyStream(Stream destinationStream, CancellationToken cancellationToken)
    {
        pipe = new Pipe();
        Stream = pipe.Writer.AsStream();
        copyTask = pipe.Reader.CopyToAsync(destinationStream, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await pipe.Writer.CompleteAsync();
        await copyTask;
        await Stream.DisposeAsync();
    }

    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();
}
