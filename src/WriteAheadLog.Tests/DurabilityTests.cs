using System.Security.Cryptography;
using Eryri.Buffers.Extensions;
using Eryri.WriteAheadLog.Tests.Domain;
using FluentAssertions;

namespace Eryri.WriteAheadLog.Tests;

[TestFixture, Parallelizable(ParallelScope.All)]
public class DurabilityTests
{
    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task Initialize_Rebuilds_From_Logs()
    {
        const decimal expectedValue = 16M;
        var directory = Directory.CreateTempSubdirectory();

        try
        {
            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);
                ctx.State.Should().Be(0);

                await ctx.AppendAndApply(new Mutation(7, MutationType.Add), CancellationToken);
                await ctx.AppendAndApply(new Mutation(5, MutationType.Mutiply), CancellationToken);
                await ctx.AppendAndApply(new Mutation(3, MutationType.Subtract), CancellationToken);
                await ctx.AppendAndApply(new Mutation(2, MutationType.Divide), CancellationToken);

                ctx.State.Should().Be(expectedValue);
            }

            using (var ctx = new Calculator(directory.FullName))
            {
                ctx.State.Should().Be(0);
                await ctx.InitializeAsync(CancellationToken);
                ctx.State.Should().Be(expectedValue);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Append_Waits_For_Initialize()
    {
        const decimal expectedValue = 16M;
        var directory = Directory.CreateTempSubdirectory();

        try
        {
            using (var ctx = new Calculator(directory.FullName))
            {
                ValueTask[] tasks = [
                    ctx.AppendAndApply(new Mutation(7, MutationType.Add), CancellationToken),
                    ctx.AppendAndApply(new Mutation(5, MutationType.Mutiply), CancellationToken),
                    ctx.AppendAndApply(new Mutation(3, MutationType.Subtract), CancellationToken),
                    ctx.AppendAndApply(new Mutation(2, MutationType.Divide), CancellationToken),
                ];

                ctx.State.Should().Be(0);
                await ctx.InitializeAsync(CancellationToken);
                await Task.WhenAll(tasks.Select(d => d.AsTask()));
                ctx.State.Should().NotBe(0);
            }

            using (var ctx = new Calculator(directory.FullName))
            {
                ctx.State.Should().Be(0);
                await ctx.InitializeAsync(CancellationToken);
                ctx.State.Should().Be(expectedValue);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Initialize_Rebuilds_From_Snapshot()
    {
        const decimal expectedValue = 16M;
        var directory = Directory.CreateTempSubdirectory();

        try
        {
            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);
                ctx.SnapshotRead.Should().BeFalse();
                ctx.State.Should().Be(0);

                await ctx.AppendAndApply(new Mutation(7, MutationType.Add), CancellationToken);
                await ctx.AppendAndApply(new Mutation(5, MutationType.Mutiply), CancellationToken);
                await ctx.AppendAndApply(new Mutation(3, MutationType.Subtract), CancellationToken);
                await ctx.AppendAndApply(new Mutation(2, MutationType.Divide), CancellationToken);
                await ctx.SaveSnapshotAsync(CancellationToken);

                ctx.State.Should().Be(expectedValue);
            }

            using (var ctx = new Calculator(directory.FullName))
            {
                ctx.State.Should().Be(0);
                await ctx.InitializeAsync(CancellationToken);
                ctx.SnapshotRead.Should().BeTrue();
                ctx.State.Should().Be(expectedValue);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Initialize_Rebuilds_From_Snapshot_And_Subsequent_Logs()
    {
        var directory = Directory.CreateTempSubdirectory();

        try
        {
            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);
                await ctx.AppendAndApply(new Mutation(10, MutationType.Add), CancellationToken);
                await ctx.SaveSnapshotAsync(CancellationToken);
                await ctx.AppendAndApply(new Mutation(5, MutationType.Add), CancellationToken);

                ctx.State.Should().Be(15);
            }

            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);

                ctx.SnapshotRead.Should().BeTrue();
                ctx.State.Should().Be(15);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Test]
    public async Task SaveSnapshot_When_Unchanged_Does_Not_Change_State()
    {
        var directory = Directory.CreateTempSubdirectory();

        try
        {
            using var ctx = new Calculator(directory.FullName);

            await ctx.InitializeAsync(CancellationToken);
            await ctx.SaveSnapshotAsync(CancellationToken);
            ctx.SnapshotsSaved.Should().Be(0);

            await ctx.AppendAndApply(new Mutation(7, MutationType.Add), CancellationToken);
            await ctx.SaveSnapshotAsync(CancellationToken);
            ctx.SnapshotsSaved.Should().Be(1);

            await ctx.SaveSnapshotAsync(CancellationToken);
            ctx.SnapshotsSaved.Should().Be(1);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Test]
    public async Task Multiple_Snapshots_Recover_Latest_State()
    {
        var directory = Directory.CreateTempSubdirectory();

        try
        {
            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);

                await ctx.AppendAndApply(new Mutation(10, MutationType.Add), CancellationToken);
                await ctx.SaveSnapshotAsync(CancellationToken);

                await ctx.AppendAndApply(new Mutation(5, MutationType.Add), CancellationToken);
                await ctx.SaveSnapshotAsync(CancellationToken);

                await ctx.AppendAndApply(new Mutation(2, MutationType.Add), CancellationToken);
            }

            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);

                ctx.State.Should().Be(17);
                ctx.SnapshotRead.Should().BeTrue();
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Test]
    public async Task Initialize_Is_Idempotent()
    {
        var directory = Directory.CreateTempSubdirectory();

        try
        {
            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);
                await ctx.AppendAndApply(new Mutation(10, MutationType.Add), CancellationToken);
            }

            using (var ctx = new Calculator(directory.FullName))
            {
                var tasks = Enumerable
                    .Range(0, 10)
                    .Select(_ => ctx.InitializeAsync(CancellationToken));

                await Task.WhenAll(tasks);

                ctx.State.Should().Be(10);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Test]
    public async Task Append_None_Is_Recovered()
    {
        var directory = Directory.CreateTempSubdirectory();

        try
        {
            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);
                await ctx.AppendAsync(new Mutation(10, MutationType.Add), Durability.Queue, CancellationToken);
                await ctx.FlushAsync(CancellationToken);
            }

            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);
                ctx.State.Should().Be(10);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Test]
    public async Task Format_Version_Change_Resets_Persisted_State()
    {
        var directory = Directory.CreateTempSubdirectory();

        try
        {
            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);
                await ctx.AppendAndApply(new Mutation(100, MutationType.Add), CancellationToken);
            }

            using (var ctx = new CalculatorV2(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);
                ctx.SnapshotRead.Should().BeFalse();
                ctx.State.Should().Be(0);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Test]
    public async Task Corrupted_Log_Entry_Is_Ignored()
    {
        var directory = Directory.CreateTempSubdirectory();

        try
        {
            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);
                await ctx.AppendAndApply(new Mutation(100, MutationType.Add), CancellationToken);
            }

            using (var fs = new FileStream(
                Path.Combine(directory.FullName, "state.wal"),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 4 << 10, // 4 KiB
                options: FileOptions.Asynchronous))
            {
                fs.Seek(0, SeekOrigin.End);
                fs.Write(RandomNumberGenerator.GetBytes(4 << 10));
            }

            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);
                ctx.State.Should().Be(100);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Test]
    public async Task Corrupted_Log_Checksum_Is_Ignored()
    {
        var directory = Directory.CreateTempSubdirectory();

        try
        {
            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);
                await ctx.AppendAndApply(new Mutation(100, MutationType.Add), CancellationToken);
            }

            using (var fs = new FileStream(
                Path.Combine(directory.FullName, "state.wal"),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 4 << 10, // 4 KiB
                options: FileOptions.Asynchronous))
            {
                fs.Seek(0, SeekOrigin.End);
                fs.Write(0);
                fs.Write(10);
            }

            using (var ctx = new Calculator(directory.FullName))
            {
                await ctx.InitializeAsync(CancellationToken);
                ctx.State.Should().Be(100);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Test]
    public async Task Throw_If_Used_After_Dispose()
    {
        var directory = Directory.CreateTempSubdirectory();
        var ctx = new Calculator(directory.FullName);

        try
        {
            ctx.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await ctx.InitializeAsync(CancellationToken));
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await ctx.AppendAsync(new Mutation(100, MutationType.Add), CancellationToken));
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await ctx.SaveSnapshotAsync(CancellationToken));
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await ctx.FlushAsync(CancellationToken));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
