namespace Eryri.WriteAheadLog.Domain;

public interface IStreamOwner : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets the Stream.
    /// </summary>
    public Stream Stream { get; }
}
