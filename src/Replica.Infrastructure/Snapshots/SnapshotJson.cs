using System.Text.Json;
using System.Text.Json.Serialization;

namespace Replica.Infrastructure.Snapshots;

internal static class SnapshotJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Serialize<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, Options);
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> json)
    {
        T? value = JsonSerializer.Deserialize<T>(json, Options);
        return value ?? throw new JsonException("Snapshot JSON contained a null root value.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = false,
            MaxDepth = 64,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            WriteIndented = true,
        };

        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
