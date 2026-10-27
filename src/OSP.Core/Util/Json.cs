using System.Text.Json;
using System.Text.Json.Serialization;

namespace OSP.Core.Util;

/// <summary>Raised when game data (parts, bodies, saves) is missing, malformed or fails validation.</summary>
public sealed class DataException : Exception
{
    public DataException(string message) : base(message) { }
    public DataException(string message, Exception inner) : base(message, inner) { }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static T Deserialize<T>(string json, string what)
    {
        try
        {
            var v = JsonSerializer.Deserialize<T>(json, Options);
            if (v == null) throw new DataException($"{what}: document is empty or null");
            return v;
        }
        catch (JsonException ex)
        {
            throw new DataException($"{what}: invalid JSON ({ex.Message})", ex);
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
