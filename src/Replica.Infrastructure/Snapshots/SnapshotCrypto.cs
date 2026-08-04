using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Replica.Core.Snapshots;

namespace Replica.Infrastructure.Snapshots;

internal static class SnapshotCrypto
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int DefaultIterations = 210_000;
    private const int DefaultChunkSize = 1024 * 1024;
    private const byte PayloadVersion = 1;
    private static readonly byte[] PayloadMagic = "RPE1"u8.ToArray();

    public static PreparedEncryption PrepareManifest(
        ReplicaSnapshotManifest manifest,
        ReadOnlyMemory<char> password)
    {
        if (password.Length < 8)
        {
            throw new ReplicaSnapshotException("Snapshot encryption requires a password of at least eight characters.");
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] key = DeriveKey(password, salt, DefaultIterations);

        try
        {
            ReplicaSnapshotEncryptionInfo provisionalInfo = new(
                Algorithm: "AES-256-GCM",
                KeyDerivation: "PBKDF2-HMAC-SHA256",
                Iterations: DefaultIterations,
                Salt: Convert.ToBase64String(salt),
                HeaderNonce: Convert.ToBase64String(nonce),
                HeaderAuthenticationTag: string.Empty,
                ChunkSize: DefaultChunkSize);
            ReplicaSnapshotManifest provisionalManifest = manifest with { Encryption = provisionalInfo };
            byte[] associatedData = SnapshotJson.Serialize(provisionalManifest);
            byte[] tag = new byte[TagSize];

            using (AesGcm aes = new(key, TagSize))
            {
                aes.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag, associatedData);
            }

            ReplicaSnapshotManifest authenticatedManifest = provisionalManifest with
            {
                Encryption = provisionalInfo with
                {
                    HeaderAuthenticationTag = Convert.ToBase64String(tag),
                },
            };

            return new PreparedEncryption(authenticatedManifest, key);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    public static byte[] VerifyManifestAndDeriveKey(
        ReplicaSnapshotManifest manifest,
        ReadOnlyMemory<char> password)
    {
        ReplicaSnapshotEncryptionInfo encryption = manifest.Encryption
            ?? throw new ReplicaSnapshotDecryptionException();

        try
        {
            if (!string.Equals(encryption.Algorithm, "AES-256-GCM", StringComparison.Ordinal) ||
                !string.Equals(encryption.KeyDerivation, "PBKDF2-HMAC-SHA256", StringComparison.Ordinal) ||
                encryption.Iterations is < 100_000 or > 2_000_000 ||
                encryption.ChunkSize is < 64 * 1024 or > 4 * 1024 * 1024 ||
                password.IsEmpty)
            {
                throw new ReplicaSnapshotDecryptionException();
            }

            byte[] salt = Convert.FromBase64String(encryption.Salt);
            byte[] nonce = Convert.FromBase64String(encryption.HeaderNonce);
            byte[] tag = Convert.FromBase64String(encryption.HeaderAuthenticationTag);

            if (salt.Length != SaltSize || nonce.Length != NonceSize || tag.Length != TagSize)
            {
                throw new ReplicaSnapshotDecryptionException();
            }

            byte[] key = DeriveKey(password, salt, encryption.Iterations);
            try
            {
                ReplicaSnapshotManifest provisionalManifest = manifest with
                {
                    Encryption = encryption with { HeaderAuthenticationTag = string.Empty },
                };
                byte[] associatedData = SnapshotJson.Serialize(provisionalManifest);

                using AesGcm aes = new(key, TagSize);
                aes.Decrypt(nonce, ReadOnlySpan<byte>.Empty, tag, Span<byte>.Empty, associatedData);
                return key;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }
        catch (ReplicaSnapshotDecryptionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException or ArgumentException)
        {
            throw new ReplicaSnapshotDecryptionException();
        }
    }

    public static async Task EncryptAsync(
        Stream source,
        Stream destination,
        byte[] key,
        ReplicaSnapshotManifest manifest,
        string entryPath,
        CancellationToken cancellationToken)
    {
        ReplicaSnapshotEncryptionInfo encryption = manifest.Encryption
            ?? throw new InvalidOperationException("Encryption metadata is missing.");

        await destination.WriteAsync(PayloadMagic, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(new byte[] { PayloadVersion }, cancellationToken).ConfigureAwait(false);
        await WriteInt32Async(destination, encryption.ChunkSize, cancellationToken).ConfigureAwait(false);

        byte[] plaintext = new byte[encryption.ChunkSize];
        byte[] ciphertext = new byte[encryption.ChunkSize];
        int frameIndex = 0;

        try
        {
            using AesGcm aes = new(key, TagSize);
            while (true)
            {
                int bytesRead = await ReadChunkAsync(source, plaintext, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
                byte[] tag = new byte[TagSize];
                byte[] associatedData = BuildEntryAssociatedData(manifest, entryPath, frameIndex);

                aes.Encrypt(
                    nonce,
                    plaintext.AsSpan(0, bytesRead),
                    ciphertext.AsSpan(0, bytesRead),
                    tag,
                    associatedData);

                await WriteInt32Async(destination, bytesRead, cancellationToken).ConfigureAwait(false);
                await destination.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
                await destination.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
                await destination.WriteAsync(ciphertext.AsMemory(0, bytesRead), cancellationToken)
                    .ConfigureAwait(false);

                CryptographicOperations.ZeroMemory(plaintext.AsSpan(0, bytesRead));
                frameIndex++;
            }

            await WriteInt32Async(destination, -1, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public static async Task DecryptAsync(
        Stream source,
        Stream destination,
        byte[] key,
        ReplicaSnapshotManifest manifest,
        string entryPath,
        long maximumPlaintextSize,
        CancellationToken cancellationToken)
    {
        try
        {
            ReplicaSnapshotEncryptionInfo encryption = manifest.Encryption
                ?? throw new ReplicaSnapshotDecryptionException();
            byte[] magic = new byte[PayloadMagic.Length];
            await ReadExactlyAsync(source, magic, cancellationToken).ConfigureAwait(false);
            int version = source.ReadByte();
            int chunkSize = await ReadInt32Async(source, cancellationToken).ConfigureAwait(false);

            if (!magic.AsSpan().SequenceEqual(PayloadMagic) ||
                version != PayloadVersion ||
                chunkSize != encryption.ChunkSize)
            {
                throw new ReplicaSnapshotDecryptionException();
            }

            byte[] ciphertext = new byte[chunkSize];
            byte[] plaintext = new byte[chunkSize];
            long totalPlaintext = 0;
            int frameIndex = 0;
            bool sawShortFrame = false;

            try
            {
                using AesGcm aes = new(key, TagSize);
                while (true)
                {
                    int frameLength = await ReadInt32Async(source, cancellationToken).ConfigureAwait(false);
                    if (frameLength == -1)
                    {
                        break;
                    }

                    if (sawShortFrame || frameLength <= 0 || frameLength > chunkSize)
                    {
                        throw new ReplicaSnapshotDecryptionException();
                    }

                    sawShortFrame = frameLength < chunkSize;

                    totalPlaintext = checked(totalPlaintext + frameLength);
                    if (totalPlaintext > maximumPlaintextSize)
                    {
                        throw new ReplicaSnapshotException("The decrypted snapshot entry exceeds the configured size limit.");
                    }

                    byte[] nonce = new byte[NonceSize];
                    byte[] tag = new byte[TagSize];
                    await ReadExactlyAsync(source, nonce, cancellationToken).ConfigureAwait(false);
                    await ReadExactlyAsync(source, tag, cancellationToken).ConfigureAwait(false);
                    await ReadExactlyAsync(source, ciphertext.AsMemory(0, frameLength), cancellationToken)
                        .ConfigureAwait(false);

                    byte[] associatedData = BuildEntryAssociatedData(manifest, entryPath, frameIndex);
                    aes.Decrypt(
                        nonce,
                        ciphertext.AsSpan(0, frameLength),
                        tag,
                        plaintext.AsSpan(0, frameLength),
                        associatedData);
                    await destination.WriteAsync(plaintext.AsMemory(0, frameLength), cancellationToken)
                        .ConfigureAwait(false);
                    CryptographicOperations.ZeroMemory(plaintext.AsSpan(0, frameLength));
                    frameIndex++;
                }

                if (source.ReadByte() != -1)
                {
                    throw new ReplicaSnapshotDecryptionException();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (ReplicaSnapshotException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException or EndOfStreamException or IOException or OverflowException)
        {
            throw new ReplicaSnapshotDecryptionException();
        }
    }

    private static byte[] DeriveKey(ReadOnlyMemory<char> password, byte[] salt, int iterations)
    {
        byte[] key = new byte[KeySize];
        Rfc2898DeriveBytes.Pbkdf2(
            password.Span,
            salt,
            key,
            iterations,
            HashAlgorithmName.SHA256);
        return key;
    }

    private static byte[] BuildEntryAssociatedData(
        ReplicaSnapshotManifest manifest,
        string entryPath,
        int frameIndex)
    {
        return Encoding.UTF8.GetBytes(
            $"{manifest.SchemaVersion}\n{manifest.SnapshotId:D}\n{entryPath}\n{frameIndex}");
    }

    private static async Task<int> ReadChunkAsync(
        Stream source,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int bytesRead = await source.ReadAsync(buffer.AsMemory(totalRead), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }

    private static async Task WriteInt32Async(
        Stream stream,
        int value,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadInt32Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int bytesRead = await stream.ReadAsync(buffer[totalRead..], cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException();
            }

            totalRead += bytesRead;
        }
    }

    internal sealed record PreparedEncryption(
        ReplicaSnapshotManifest Manifest,
        byte[] Key);
}
