using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Replica.Core.Services;
using Replica.Core.Snapshots;
using Replica.Infrastructure.Snapshots;

namespace Replica.Infrastructure.Tests.Snapshots;

public sealed class ReplicaSnapshotSecurityTests
{
    [Fact]
    public async Task ChecksumMismatchIsRejected()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "tampered.replica");
        await context.CreateWriter().WriteAsync(
            context.CreateRequest(destination, SnapshotType.Lightweight),
            progress: null,
            CancellationToken.None);
        ReplaceZipEntry(destination, "inventory/applications.json", "[]"u8.ToArray());

        ReplicaSnapshotException exception = await Assert.ThrowsAsync<ReplicaSnapshotException>(
            () => context.CreateReader().ReadAsync(
                new ReplicaSnapshotReadRequest(destination),
                progress: null,
                CancellationToken.None));

        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorruptZipIsRejected()
    {
        using SnapshotTestContext context = new();
        string destination = context.CreateFile("corrupt.replica", "not a zip archive");

        await Assert.ThrowsAsync<ReplicaSnapshotException>(
            () => context.CreateReader().ReadAsync(
                new ReplicaSnapshotReadRequest(destination),
                progress: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task DeterministicFuzzCorpusFailsClosed()
    {
        using SnapshotTestContext context = new();
        for (int seed = 0; seed < 32; seed++)
        {
            string destination = Path.Combine(context.RootPath, $"fuzz-{seed}.replica");
            Random random = new(seed);
            byte[] payload = new byte[random.Next(0, 4096)];
            random.NextBytes(payload);
            await File.WriteAllBytesAsync(destination, payload);

            await Assert.ThrowsAsync<ReplicaSnapshotException>(() => context.CreateReader().ReadAsync(
                new ReplicaSnapshotReadRequest(destination),
                progress: null,
                CancellationToken.None));
        }
    }

    [Fact]
    public async Task JsonBeyondMaximumDepthIsRejected()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "deep.replica");
        await context.CreateWriter().WriteAsync(
            context.CreateRequest(destination, SnapshotType.Lightweight),
            progress: null,
            CancellationToken.None);
        string manifest = Encoding.UTF8.GetString(ReadZipEntry(destination, "manifest.json"));
        string nested = string.Concat(Enumerable.Repeat("{\"item\":", 70)) +
            "0" + new string('}', 70);
        int end = manifest.LastIndexOf('}');
        ReplaceZipEntry(
            destination,
            "manifest.json",
            Encoding.UTF8.GetBytes(manifest.Insert(end, $",\"attack\":{nested}")));

        await Assert.ThrowsAsync<ReplicaSnapshotException>(() => context.CreateReader().ReadAsync(
            new ReplicaSnapshotReadRequest(destination),
            progress: null,
            CancellationToken.None));
    }

    [Fact]
    public async Task OversizedJsonStringIsRejectedBeforeDeserialization()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "large-string.replica");
        await context.CreateWriter().WriteAsync(
            context.CreateRequest(destination, SnapshotType.Lightweight),
            progress: null,
            CancellationToken.None);
        string manifest = Encoding.UTF8.GetString(ReadZipEntry(destination, "manifest.json"));
        int end = manifest.LastIndexOf('}');
        ReplaceZipEntry(
            destination,
            "manifest.json",
            Encoding.UTF8.GetBytes(manifest.Insert(end, $",\"attack\":\"{new string('x', 8192)}\"")));
        ReplicaSnapshotReadLimits limits = ReplicaSnapshotReadLimits.Default with
        {
            MaximumJsonSize = 4096,
        };

        await Assert.ThrowsAsync<ReplicaSnapshotException>(() => context.CreateReader(limits).ReadAsync(
            new ReplicaSnapshotReadRequest(destination),
            progress: null,
            CancellationToken.None));
    }

    [Fact]
    public async Task UnsupportedSchemaIsRejectedBeforeInventoryUse()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "old-schema.replica");
        await context.CreateWriter().WriteAsync(
            context.CreateRequest(destination, SnapshotType.Lightweight),
            progress: null,
            CancellationToken.None);
        JsonObject manifest = JsonNode.Parse(ReadZipEntry(destination, "manifest.json"))!.AsObject();
        manifest["schemaVersion"] = "0.0";
        ReplaceZipEntry(destination, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));

        ReplicaSnapshotException exception = await Assert.ThrowsAsync<ReplicaSnapshotException>(() =>
            context.CreateReader().ReadAsync(
                new ReplicaSnapshotReadRequest(destination),
                progress: null,
                CancellationToken.None));

        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateChecksumRecordCannotBypassIntegrityIndex()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "duplicate-checksum.replica");
        await context.CreateWriter().WriteAsync(
            context.CreateRequest(destination, SnapshotType.Lightweight),
            progress: null,
            CancellationToken.None);
        JsonArray checksums = JsonNode.Parse(ReadZipEntry(destination, "checksums.json"))!.AsArray();
        checksums.Add(checksums[0]!.DeepClone());
        ReplaceZipEntry(destination, "checksums.json", Encoding.UTF8.GetBytes(checksums.ToJsonString()));

        await Assert.ThrowsAsync<ReplicaSnapshotException>(() => context.CreateReader().ReadAsync(
            new ReplicaSnapshotReadRequest(destination),
            progress: null,
            CancellationToken.None));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("inventory\\windows.json")]
    [InlineData("files/CON.txt")]
    [InlineData("files/trailing-dot.")]
    public async Task UnsafeArchivePathsAreRejected(string unsafePath)
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "unsafe.replica");
        CreateZipWithEntries(destination, [(unsafePath, "unsafe"u8.ToArray())]);

        await Assert.ThrowsAsync<ReplicaSnapshotException>(
            () => context.CreateReader().ReadAsync(
                new ReplicaSnapshotReadRequest(destination),
                progress: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task CaseCollidingEntriesAreRejected()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "duplicate.replica");
        CreateZipWithEntries(
            destination,
            [
                ("manifest.json", "{}"u8.ToArray()),
                ("MANIFEST.JSON", "{}"u8.ToArray()),
            ]);

        await Assert.ThrowsAsync<ReplicaSnapshotException>(
            () => context.CreateReader().ReadAsync(
                new ReplicaSnapshotReadRequest(destination),
                progress: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task CompressionBombLikeEntryIsRejectedByRatioLimit()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "bomb.replica");
        CreateZipWithEntries(destination, [("bomb.bin", new byte[1024 * 1024])]);
        ReplicaSnapshotReadLimits strictLimits = new(
            MaximumEntryCount: 100,
            MaximumEntrySize: 2 * 1024 * 1024,
            MaximumTotalUncompressedSize: 2 * 1024 * 1024,
            MaximumCompressionRatio: 10,
            MaximumPathDepth: 32,
            MaximumJsonSize: 1024 * 1024);

        ReplicaSnapshotException exception = await Assert.ThrowsAsync<ReplicaSnapshotException>(
            () => context.CreateReader(strictLimits).ReadAsync(
                new ReplicaSnapshotReadRequest(destination),
                progress: null,
                CancellationToken.None));

        Assert.Contains("compression ratio", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EntryCountAndTotalSizeLimitsAreEnforced()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "bounded.replica");
        CreateZipWithEntries(
            destination,
            [
                ("one.bin", new byte[16]),
                ("two.bin", new byte[16]),
                ("three.bin", new byte[16]),
            ]);
        ReplicaSnapshotReadLimits countLimits = new(2, 1024, 4096, 1000, 32, 1024);
        ReplicaSnapshotReadLimits sizeLimits = new(10, 1024, 20, 1000, 32, 1024);

        await Assert.ThrowsAsync<ReplicaSnapshotException>(
            () => context.CreateReader(countLimits).ReadAsync(
                new ReplicaSnapshotReadRequest(destination),
                progress: null,
                CancellationToken.None));
        await Assert.ThrowsAsync<ReplicaSnapshotException>(
            () => context.CreateReader(sizeLimits).ReadAsync(
                new ReplicaSnapshotReadRequest(destination),
                progress: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task ReparsePointArchiveEntryIsRejected()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "reparse.replica");
        using (FileStream stream = new(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("payload.bin");
            entry.ExternalAttributes = (int)FileAttributes.ReparsePoint;
            using Stream entryStream = entry.Open();
            entryStream.WriteByte(1);
        }

        await Assert.ThrowsAsync<ReplicaSnapshotException>(
            () => context.CreateReader().ReadAsync(
                new ReplicaSnapshotReadRequest(destination),
                progress: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task EncryptedSnapshotReadsWithCorrectPassword()
    {
        using SnapshotTestContext context = new();
        string selectedDirectory = context.CreateDirectory("encrypted-input");
        await File.WriteAllTextAsync(Path.Combine(selectedDirectory, "save.dat"), "encrypted payload");
        string destination = Path.Combine(context.RootPath, "encrypted.replica");
        char[] password = "correct horse battery".ToCharArray();
        ReplicaSnapshotWriteRequest request = context.CreateRequest(
            destination,
            SnapshotType.Recovery,
            [new ReplicaSelectedFolder(
                selectedDirectory,
                "selected/saves",
                ReplicaSelectedFolderCategory.GameSave)],
            encryption: new ReplicaSnapshotEncryptionOptions(password));

        await context.CreateWriter().WriteAsync(request, progress: null, CancellationToken.None);
        ReplicaSnapshotReadResult result = await context.CreateReader().ReadAsync(
            new ReplicaSnapshotReadRequest(destination, password),
            progress: null,
            CancellationToken.None);
        byte[] encryptedInventory = ReadZipEntry(destination, "inventory/applications.json");
        byte[] encryptedEnvironment = ReadZipEntry(destination, "inventory/environment.json");
        Array.Clear(password);

        Assert.NotNull(result.Manifest.Encryption);
        Assert.Contains("files/selected/saves/save.dat", result.EntryPaths);
        Assert.True(encryptedInventory.AsSpan(0, 4).SequenceEqual("RPE1"u8));
        Assert.Equal(-1, encryptedInventory.AsSpan().IndexOf("Example App"u8));
        Assert.False(encryptedInventory.AsSpan(13, 12).SequenceEqual(encryptedEnvironment.AsSpan(13, 12)));
    }

    [Fact]
    public async Task WrongPasswordReturnsOnlyGenericDecryptionError()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "wrong-password.replica");
        char[] password = "correct password".ToCharArray();
        ReplicaSnapshotWriteRequest request = context.CreateRequest(
            destination,
            SnapshotType.Lightweight,
            encryption: new ReplicaSnapshotEncryptionOptions(password));
        await context.CreateWriter().WriteAsync(request, progress: null, CancellationToken.None);

        ReplicaSnapshotDecryptionException exception = await Assert.ThrowsAsync<ReplicaSnapshotDecryptionException>(
            () => context.CreateReader().ReadAsync(
                new ReplicaSnapshotReadRequest(destination, "wrong password".AsMemory()),
                progress: null,
                CancellationToken.None));
        Array.Clear(password);

        Assert.Equal("The snapshot could not be decrypted.", exception.Message);
        Assert.DoesNotContain("wrong password", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SERVICE_TOKEN")]
    [InlineData("SIGNING_KEY")]
    [InlineData("DATABASE_CREDENTIAL")]
    [InlineData("DOCKER_AUTH_CONFIG")]
    public async Task SensitiveEnvironmentVariableIsRejectedWithoutExposingItsValue(string variableName)
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "sensitive-environment.replica");
        ReplicaSnapshotWriteRequest request = context.CreateRequest(destination, SnapshotType.Lightweight);
        request = request with
        {
            Inventory = request.Inventory with
            {
                Environment = request.Inventory.Environment with
                {
                    Variables = [new ReplicaEnvironmentVariable(variableName, "do-not-expose", "User")],
                },
            },
        };

        ReplicaSnapshotException exception = await Assert.ThrowsAsync<ReplicaSnapshotException>(
            () => context.CreateWriter().WriteAsync(request, progress: null, CancellationToken.None));

        Assert.DoesNotContain("do-not-expose", exception.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task ReaderRejectsRewrittenSensitiveEnvironmentVariableWithValidChecksums()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "rewritten-sensitive.replica");
        await context.CreateWriter().WriteAsync(
            context.CreateRequest(destination, SnapshotType.Lightweight),
            progress: null,
            CancellationToken.None);
        JsonObject environment = JsonNode.Parse(
            ReadZipEntry(destination, "inventory/environment.json"))!.AsObject();
        JsonObject variable = environment["variables"]!.AsArray()[0]!.AsObject();
        variable["name"] = "SIGNING_KEY";
        variable["value"] = "never-display";
        byte[] rewritten = Encoding.UTF8.GetBytes(environment.ToJsonString());
        ReplaceZipEntry(destination, "inventory/environment.json", rewritten);

        JsonArray checksums = JsonNode.Parse(ReadZipEntry(destination, "checksums.json"))!.AsArray();
        JsonObject checksum = checksums
            .Select(node => node!.AsObject())
            .Single(node => string.Equals(
                node["entryPath"]!.GetValue<string>(),
                "inventory/environment.json",
                StringComparison.Ordinal));
        checksum["value"] = Convert.ToHexString(SHA256.HashData(rewritten));
        ReplaceZipEntry(destination, "checksums.json", Encoding.UTF8.GetBytes(checksums.ToJsonString()));

        ReplicaSnapshotException exception = await Assert.ThrowsAsync<ReplicaSnapshotException>(() =>
            context.CreateReader().ReadAsync(
                new ReplicaSnapshotReadRequest(destination),
                progress: null,
                CancellationToken.None));

        Assert.DoesNotContain("never-display", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentSchemaRejectsConflictingPlatformMetadata()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "platform-conflict.replica");
        await context.CreateWriter().WriteAsync(
            context.CreateRequest(destination, SnapshotType.Lightweight),
            progress: null,
            CancellationToken.None);
        JsonObject manifest = JsonNode.Parse(ReadZipEntry(destination, "manifest.json"))!.AsObject();
        manifest["sourcePlatform"] = "MacOS";
        ReplaceZipEntry(destination, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));

        ReplicaSnapshotException exception = await Assert.ThrowsAsync<ReplicaSnapshotException>(() =>
            context.CreateReader().ReadAsync(
                new ReplicaSnapshotReadRequest(destination),
                progress: null,
                CancellationToken.None));

        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriterRejectsInternallyInconsistentPlatformMetadata()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "platform-input-conflict.replica");
        ReplicaSnapshotWriteRequest request = context.CreateRequest(destination, SnapshotType.Lightweight);
        ReplicaWindowsInfo windows = request.Machine.Windows;
        request = request with
        {
            Machine = request.Machine with
            {
                Platform = new ReplicaPlatformInfo(
                    ReplicaPlatformFamily.Windows,
                    windows.Edition,
                    windows.Version,
                    windows.Build,
                    "arm64",
                    windows.Locale,
                    windows.TimeZone,
                    windows.Capabilities),
            },
        };

        await Assert.ThrowsAsync<ReplicaSnapshotException>(() => context.CreateWriter().WriteAsync(
            request,
            progress: null,
            CancellationToken.None));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task WriterRejectsAnEstimatorThatReturnsAnUnselectedFile()
    {
        using SnapshotTestContext context = new();
        string selectedDirectory = context.CreateDirectory("approved");
        string unselectedFile = context.CreateFile("unapproved/private.txt", "not approved");
        string destination = Path.Combine(context.RootPath, "unapproved.replica");
        ReplicaSnapshotWriteRequest request = context.CreateRequest(
            destination,
            SnapshotType.Recovery,
            [new ReplicaSelectedFolder(
                selectedDirectory,
                "selected/approved",
                ReplicaSelectedFolderCategory.Project)]);
        FakeEstimator estimator = new(
            new ReplicaSelectionEstimate(
                [new ReplicaFileEstimate(unselectedFile, "files/selected/approved/private.txt", 12)],
                [],
                12));

        await Assert.ThrowsAsync<ReplicaSnapshotException>(
            () => context.CreateWriter(estimator: estimator).WriteAsync(
                request,
                progress: null,
                CancellationToken.None));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task BroadRootSelectionIsRejectedBeforeFilesAreEnumerated()
    {
        using SnapshotTestContext context = new();
        string driveRoot = Path.GetPathRoot(context.RootPath)
            ?? throw new InvalidOperationException("Test drive root was unavailable.");
        SnapshotSelectionEstimator estimator = new();

        await Assert.ThrowsAsync<ReplicaSnapshotException>(
            () => estimator.EstimateAsync(
                [new ReplicaSelectedFolder(
                    driveRoot,
                    "selected/root",
                    ReplicaSelectedFolderCategory.Documents)],
                [],
                CancellationToken.None));
    }

    [Fact]
    public async Task CancellationLeavesNoDestinationOrTemporaryFiles()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "cancelled.replica");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.CreateWriter().WriteAsync(
                context.CreateRequest(destination, SnapshotType.Lightweight),
                progress: null,
                cancellation.Token));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(context.RootPath));
    }

    [Fact]
    public async Task CancellationDuringCopyCleansPartialStagingData()
    {
        using SnapshotTestContext context = new();
        string selectedDirectory = context.CreateDirectory("cancel-input");
        await File.WriteAllBytesAsync(Path.Combine(selectedDirectory, "one.bin"), new byte[1024]);
        await File.WriteAllBytesAsync(Path.Combine(selectedDirectory, "two.bin"), new byte[1024]);
        string destination = Path.Combine(context.RootPath, "cancelled-during-copy.replica");
        using CancellationTokenSource cancellation = new();
        InlineProgress progress = new(value =>
        {
            if (value.Stage == ReplicaSnapshotStage.Staging && value.CompletedItems == 1)
            {
                cancellation.Cancel();
            }
        });
        ReplicaSnapshotWriteRequest request = context.CreateRequest(
            destination,
            SnapshotType.Recovery,
            [new ReplicaSelectedFolder(
                selectedDirectory,
                "selected/cancel",
                ReplicaSelectedFolderCategory.Project)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.CreateWriter().WriteAsync(request, progress, cancellation.Token));

        Assert.False(File.Exists(destination));
        Assert.DoesNotContain(
            Directory.GetFileSystemEntries(context.RootPath),
            path => Path.GetFileName(path).StartsWith(".cancelled-during-copy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidationFailureCleansStagingAndIncompleteArchive()
    {
        using SnapshotTestContext context = new();
        string destination = Path.Combine(context.RootPath, "failed.replica");
        ReplicaSnapshotWriter writer = context.CreateWriter(reader: new ThrowingReader());

        await Assert.ThrowsAsync<ReplicaSnapshotException>(
            () => writer.WriteAsync(
                context.CreateRequest(destination, SnapshotType.Lightweight),
                progress: null,
                CancellationToken.None));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(context.RootPath));
    }

    private static void ReplaceZipEntry(string archivePath, string entryPath, byte[] content)
    {
        using FileStream stream = new(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Update);
        ZipArchiveEntry existingEntry = archive.GetEntry(entryPath)
            ?? throw new InvalidOperationException("Expected ZIP entry was not found.");
        existingEntry.Delete();
        ZipArchiveEntry replacement = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using Stream replacementStream = replacement.Open();
        replacementStream.Write(content);
    }

    private static byte[] ReadZipEntry(string archivePath, string entryPath)
    {
        using FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        ZipArchiveEntry entry = archive.GetEntry(entryPath)
            ?? throw new InvalidOperationException("Expected ZIP entry was not found.");
        using Stream entryStream = entry.Open();
        using MemoryStream output = new();
        entryStream.CopyTo(output);
        return output.ToArray();
    }

    private static void CreateZipWithEntries(
        string archivePath,
        IReadOnlyList<(string EntryPath, byte[] Content)> entries)
    {
        using FileStream stream = new(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        foreach ((string entryPath, byte[] content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
            using Stream entryStream = entry.Open();
            entryStream.Write(content);
        }
    }

    private sealed class ThrowingReader : ISnapshotReader
    {
        public Task<ReplicaSnapshotReadResult> ReadAsync(
            ReplicaSnapshotReadRequest request,
            IProgress<ReplicaSnapshotProgress>? progress,
            CancellationToken cancellationToken)
        {
            throw new ReplicaSnapshotException("Synthetic validation failure.");
        }
    }

    private sealed class InlineProgress : IProgress<ReplicaSnapshotProgress>
    {
        private readonly Action<ReplicaSnapshotProgress> handler;

        public InlineProgress(Action<ReplicaSnapshotProgress> handler)
        {
            this.handler = handler;
        }

        public void Report(ReplicaSnapshotProgress value)
        {
            handler(value);
        }
    }

    private sealed class FakeEstimator : ISnapshotSelectionEstimator
    {
        private readonly ReplicaSelectionEstimate estimate;

        public FakeEstimator(ReplicaSelectionEstimate estimate)
        {
            this.estimate = estimate;
        }

        public Task<ReplicaSelectionEstimate> EstimateAsync(
            IReadOnlyList<ReplicaSelectedFolder> selectedFolders,
            IReadOnlyList<ReplicaOfflineInstaller> offlineInstallers,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(estimate);
        }
    }
}
