namespace Eryri.WriteAheadLog;

public enum Durability
{
    /// <summary>Queue the record without waiting for persistence.</summary>
    Queue,

    /// <summary>Wait until the record has been written to the stream.</summary>
    Write,

    /// <summary>Wait until the record has been flushed.</summary>
    Flush,

    /// <summary>Wait until the record has been written to disk.</summary>
    Durable
}
