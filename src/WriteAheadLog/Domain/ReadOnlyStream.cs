using System.IO.Pipelines;

namespace Eryri.WriteAheadLog.Domain;

public sealed class ReadOnlyStream : IStreamOwner, IAsyncDisposable, IDisposable
{
    public Stream Stream { get; private init; }
    private readonly Pipe pipe;
    private readonly Task copyTask;
    public ReadOnlyStream(Stream sourceStream, CancellationToken cancellationToken)
    {
        pipe = new Pipe();
        copyTask = sourceStream.CopyToAsync(pipe.Writer, cancellationToken);
        Stream = pipe.Reader.AsStream();
    }

    public async ValueTask DisposeAsync()
    {
        await pipe.Writer.CompleteAsync();
        await copyTask;
        await Stream.DisposeAsync();
    }

    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();
}
