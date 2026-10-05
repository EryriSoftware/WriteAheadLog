namespace Eryri.WriteAheadLog.Models;

internal record struct Checkpoint(Guid Generation, long Position);
