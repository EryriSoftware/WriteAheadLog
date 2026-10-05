using System.Buffers;
using Eryri.Buffers.Extensions;

namespace Eryri.WriteAheadLog.Tests.Domain;

internal class Calculator(string directory) : WriteAheadLog<Mutation>(directory)
{
    public decimal State;
    public bool SnapshotRead { get; private set; }
    public int SnapshotsSaved { get; private set; }
    protected override ulong FormatVersion => 1;

    public async ValueTask AppendAndApply(Mutation record, CancellationToken cancellationToken)
    {
        await AppendAsync(record, Durability.Write, cancellationToken);
        await ApplyAsync(record, cancellationToken);
    }

    protected override ValueTask ApplyAsync(Mutation record, CancellationToken cancellationToken)
    {
        switch (record.Type)
        {
            case MutationType.Add:
                State += record.Value;
                break;

            case MutationType.Subtract:
                State -= record.Value;
                break;

            case MutationType.Mutiply:
                State *= record.Value;
                break;

            case MutationType.Divide:
                State /= record.Value;
                break;

            default:
                throw new ArgumentOutOfRangeException($"Unrecognised type: {record.Type}");
        }

        return ValueTask.CompletedTask;
    }

    protected override Mutation Deserialize(ReadOnlyMemory<byte> record)
    {
        var read = 0;
        read += record.Span.Slice(read).ReadByte(out var type);
        read += record.Span.Slice(read).ReadDecimal(out var value);

        return new Mutation(value, (MutationType)type);
    }

    protected override void Serialize(Mutation record, IBufferWriter<byte> buffer)
    {
        buffer.Write((byte)record.Type);
        buffer.Write(record.Value);
    }

    protected override ValueTask ReadSnapshotAsync(Stream stream, CancellationToken cancellationToken)
    {
        SnapshotRead = true;
        State = stream.ReadDecimal();
        return ValueTask.CompletedTask;
    }

    protected override ValueTask WriteSnapshotAsync(Stream stream, CancellationToken cancellationToken)
    {
        SnapshotsSaved++;
        stream.Write(State);
        return ValueTask.CompletedTask;
    }
}
