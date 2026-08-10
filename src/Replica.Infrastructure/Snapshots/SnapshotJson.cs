using System.Text.Json;
using System.Text.Json.Serialization;

namespace Replica.Infrastructure.Snapshots;

internal static class SnapshotJson
{
    private const int MaximumPropertyNameBytes = 1024;
    private const int MaximumStringValueBytes = 1024 * 1024;

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Serialize<T>(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        ValidateTokenSizes(json);
        return json;
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> json)
    {
        ValidateTokenSizes(json);
        T? value = JsonSerializer.Deserialize<T>(json, Options);
        return value ?? throw new JsonException("Snapshot JSON contained a null root value.");
    }

    private static void ValidateTokenSizes(ReadOnlySpan<byte> json)
    {
        Utf8JsonReader reader = new(json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = Options.MaxDepth,
        });
        while (reader.Read())
        {
            long length = reader.HasValueSequence
                ? reader.ValueSequence.Length
                : reader.ValueSpan.Length;
            if (reader.TokenType == JsonTokenType.PropertyName && length > MaximumPropertyNameBytes ||
                reader.TokenType == JsonTokenType.String && length > MaximumStringValueBytes)
            {
                throw new JsonException("Snapshot JSON contains an oversized string token.");
            }
        }
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
