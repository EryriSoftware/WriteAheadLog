using Eryri.Buffers.Extensions;
using Eryri.WriteAheadLog.Models;

namespace Eryri.WriteAheadLog.Extensions;

internal static class StreamExtensions
{
    extension(Stream stream)
    {
        public void Write(Checkpoint checkpoint)
        {
            stream.Write(checkpoint.Generation);
            stream.Write(checkpoint.Position);
        }

        public Checkpoint ReadCheckpoint() =>
            new Checkpoint(
            Generation: stream.ReadGuid(),
            Position: stream.ReadLong());
    }
}
