using Eryri.Buffers.Extensions;
using Eryri.WriteAheadLog.Domain;
using Eryri.WriteAheadLog.Models;

namespace Eryri.WriteAheadLog.Extensions;

internal static class StreamExtensions
{
    extension(Stream stream)
    {
        internal void Write(Checkpoint checkpoint)
        {
            stream.Write(checkpoint.Generation);
            stream.Write(checkpoint.Position);
        }

        internal Checkpoint ReadCheckpoint() =>
            new Checkpoint(
            Generation: stream.ReadGuid(),
            Position: stream.ReadLong());

        public IStreamOwner AsReadOnly(CancellationToken cancellationToken = default) => new ReadOnlyStream(stream, cancellationToken);
        public IStreamOwner AsWriteOnly(CancellationToken cancellationToken = default) => new WriteOnlyStream(stream, cancellationToken);
    }
}
