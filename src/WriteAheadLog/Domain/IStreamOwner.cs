namespace Eryri.WriteAheadLog.Domain;

internal interface IStreamOwner : IAsyncDisposable, IDisposable
{
    public Stream Stream { get; }
}
