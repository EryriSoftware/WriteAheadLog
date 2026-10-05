namespace Eryri.WriteAheadLog.Models;

internal abstract record class Command
{
    private readonly TaskCompletionSource TaskSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Complete() => TaskSource.SetResult();
    public void Fail(Exception ex) => TaskSource.SetException(ex);
    public Task Task => TaskSource.Task;
}

internal record AppendCommand<T>(T Value, Durability durability) : Command;
internal record FlushCommand(bool flushToDisk = false) : Command;
internal record SaveCommand : Command;