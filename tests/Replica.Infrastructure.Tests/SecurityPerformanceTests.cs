using System.Diagnostics;
using Replica.Core.Plugins;
using Replica.Core.Snapshots;
using Replica.Infrastructure.Tests.Snapshots;

namespace Replica.Infrastructure.Tests;

public sealed class SecurityPerformanceTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public void PluginSnapshot_ValidatesThousandsOfExtensionArtifacts()
    {
        const int extensionCount = 5_000;
        PluginCapturedFile[] extensions = Enumerable.Range(0, extensionCount)
            .Select(index => new PluginCapturedFile(
                $"extensions/vendor.extension-{index:D4}.json",
                $"{{\"version\":\"1.0.{index}\"}}",
                "application/json"))
            .ToArray();
        PluginSnapshot snapshot = new(
            "built-in.vscode",
            "1.0.0",
            new Dictionary<string, string>(),
            extensions,
            [],
            []);
        Stopwatch stopwatch = Stopwatch.StartNew();

        ReplicaPluginSnapshot converted = snapshot.ToReplicaSnapshot();

        stopwatch.Stop();
        Assert.Equal(extensionCount, converted.Files!.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), stopwatch.Elapsed.ToString());
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task RecoverySnapshot_StreamsLargePayloadAndLeavesNoTemporaryFiles()
    {
        const int payloadSize = 32 * 1024 * 1024;
        using SnapshotTestContext context = new();
        string selected = context.CreateDirectory("large-recovery");
        string payload = Path.Combine(selected, "project.bin");
        await WriteDeterministicPayloadAsync(payload, payloadSize);
        string destination = Path.Combine(context.RootPath, "large-recovery.replica");
        ReplicaSnapshotWriteRequest request = context.CreateRequest(
            destination,
            SnapshotType.Recovery,
            [new ReplicaSelectedFolder(
                selected,
                "selected/project",
                ReplicaSelectedFolderCategory.Project)]);
        Stopwatch stopwatch = Stopwatch.StartNew();

        await context.CreateWriter().WriteAsync(request, progress: null, CancellationToken.None);
        ReplicaSnapshotReadResult result = await context.CreateReader().ReadAsync(
            new ReplicaSnapshotReadRequest(destination),
            progress: null,
            CancellationToken.None);

        stopwatch.Stop();
        ReplicaArtifact artifact = Assert.Single(result.Manifest.Metadata.Artifacts);
        Assert.Equal(payloadSize, artifact.Size);
        Assert.Equal(64, artifact.Sha256.Length);
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(context.RootPath),
            path => Path.GetFileName(path).StartsWith(".large-recovery", StringComparison.Ordinal));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), stopwatch.Elapsed.ToString());
    }

    private static async Task WriteDeterministicPayloadAsync(string path, int size)
    {
        byte[] buffer = new byte[128 * 1024];
        Random random = new(42);
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        int remaining = size;
        while (remaining > 0)
        {
            random.NextBytes(buffer);
            int count = Math.Min(buffer.Length, remaining);
            await stream.WriteAsync(buffer.AsMemory(0, count));
            remaining -= count;
        }
    }
}
