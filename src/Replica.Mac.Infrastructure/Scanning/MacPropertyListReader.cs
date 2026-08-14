using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Replica.Mac.Infrastructure.Scanning;

public interface IMacPropertyListReader
{
    Task<IReadOnlyDictionary<string, string>> ReadStringDictionaryAsync(
        string path,
        CancellationToken cancellationToken);
}

public interface IMacBinaryPropertyListConverter
{
    Task<byte[]> ConvertToXmlAsync(string path, CancellationToken cancellationToken);
}

public sealed class MacPropertyListReader : IMacPropertyListReader
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly byte[] BinaryHeader = "bplist00"u8.ToArray();
    private readonly IMacBinaryPropertyListConverter binaryConverter;

    public MacPropertyListReader()
        : this(new PlutilBinaryPropertyListConverter())
    {
    }

    public MacPropertyListReader(IMacBinaryPropertyListConverter binaryConverter)
    {
        this.binaryConverter = binaryConverter;
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadStringDictionaryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        FileInfo file = new(fullPath);
        if (!file.Exists || file.Length is < 1 or > MaximumBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new XmlException("The property list is unavailable or exceeds the size limit.");
        }

        byte[] content = new byte[file.Length];
        await using (FileStream stream = new(
                         fullPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        }

        if (content.AsSpan().StartsWith(BinaryHeader))
        {
            content = await binaryConverter.ConvertToXmlAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (content.Length is < 1 or > MaximumBytes)
            {
                throw new XmlException("The converted property list exceeds the size limit.");
            }
        }

        return ParseXml(content);
    }

    private static IReadOnlyDictionary<string, string> ParseXml(byte[] content)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaximumBytes,
            XmlResolver = null,
        };

        using MemoryStream stream = new(content, writable: false);
        using XmlReader reader = XmlReader.Create(stream, settings);
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        XElement? dictionary = document.Root?.Element("dict");
        if (dictionary is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        List<XElement> elements = dictionary.Elements().ToList();
        for (int index = 0; index + 1 < elements.Count; index += 2)
        {
            XElement key = elements[index];
            XElement value = elements[index + 1];
            if (key.Name.LocalName == "key" &&
                value.Name.LocalName is "string" or "integer" &&
                !string.IsNullOrWhiteSpace(key.Value))
            {
                values[key.Value] = value.Value;
            }
        }

        return values;
    }
}

public sealed class PlutilBinaryPropertyListConverter : IMacBinaryPropertyListConverter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const int MaximumOutputBytes = 2 * 1024 * 1024;
    private const string PlutilPath = "/usr/bin/plutil";

    public async Task<byte[]> ConvertToXmlAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsMacOS() || !File.Exists(PlutilPath))
        {
            throw new XmlException("Binary property lists require the macOS property-list converter.");
        }

        ProcessStartInfo startInfo = new(PlutilPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-convert");
        startInfo.ArgumentList.Add("xml1");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("-");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(Path.GetFullPath(path));

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new XmlException("The property-list converter could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new XmlException("The property-list converter could not be started.", exception);
        }

        using CancellationTokenSource timeout = new(Timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        Task<byte[]> stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, linked.Token);
        Task<byte[]> stderr = ReadBoundedAsync(process.StandardError.BaseStream, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            byte[] output = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 || output.Length == 0)
            {
                throw new XmlException("The property-list converter rejected the input.");
            }

            return output;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("The property-list converter timed out.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using MemoryStream output = new();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > MaximumOutputBytes)
            {
                throw new XmlException("The property-list converter output exceeds the size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
