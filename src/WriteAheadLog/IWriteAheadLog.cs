namespace Eryri.WriteAheadLog;

public interface IWriteAheadLog<T>
{
    public Task InitializeAsync(CancellationToken cancellationToken);
    public ValueTask AppendAsync(T record, CancellationToken cancellationToken);
    public ValueTask AppendAsync(T record, Durability durability, CancellationToken cancellationToken);
    public Task SaveSnapshotAsync(CancellationToken cancellationToken);
    public Task FlushAsync(CancellationToken cancellationToken, bool flushToDisk = false);
}