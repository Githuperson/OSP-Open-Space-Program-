using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using OSP.Core.Math;

namespace OSP.Core.Persistence
{
    internal static class JsonNumbers
    {
        /// <summary>Reads a number, accepting "NaN"/"Infinity" strings so corrupt values survive parsing and can be validated.</summary>
        public static double Read(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                string s = reader.GetString();
                return s switch
                {
                    "NaN" => double.NaN,
                    "Infinity" => double.PositiveInfinity,
                    "-Infinity" => double.NegativeInfinity,
                    _ => throw new JsonException($"Expected a number, got '{s}'."),
                };
            }
            return reader.GetDouble();
        }

        public static void Write(Utf8JsonWriter writer, double v)
        {
            if (double.IsFinite(v)) writer.WriteNumberValue(v);
            else writer.WriteStringValue(double.IsNaN(v) ? "NaN" : (v > 0 ? "Infinity" : "-Infinity"));
        }
    }

    /// <summary>Serialises <see cref="Vector3d"/> as a compact [x, y, z] array.</summary>
    public sealed class Vector3dConverter : JsonConverter<Vector3d>
    {
        public override Vector3d Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected [x, y, z] array for vector.");
            double[] v = new double[3];
            int i = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    if (i != 3) throw new JsonException("Vector must have exactly 3 components.");
                    return new Vector3d(v[0], v[1], v[2]);
                }
                if (i >= 3) throw new JsonException("Vector must have exactly 3 components.");
                v[i++] = JsonNumbers.Read(ref reader);
            }
            throw new JsonException("Unterminated vector array.");
        }

        public override void Write(Utf8JsonWriter writer, Vector3d value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            JsonNumbers.Write(writer, value.X);
            JsonNumbers.Write(writer, value.Y);
            JsonNumbers.Write(writer, value.Z);
            writer.WriteEndArray();
        }
    }

    /// <summary>Serialises <see cref="QuaternionD"/> as [x, y, z, w].</summary>
    public sealed class QuaternionDConverter : JsonConverter<QuaternionD>
    {
        public override QuaternionD Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected [x, y, z, w] array for quaternion.");
            double[] v = new double[4];
            int i = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    if (i != 4) throw new JsonException("Quaternion must have exactly 4 components.");
                    var q = new QuaternionD(v[0], v[1], v[2], v[3]);
                    // Invalid rotations are returned as-is (non-finite) so validators can repair them with a warning.
                    if (!q.IsFinite || q.Length < 1e-9) return new QuaternionD(double.NaN, 0, 0, 0);
                    return q.Normalized();
                }
                if (i >= 4) throw new JsonException("Quaternion must have exactly 4 components.");
                v[i++] = JsonNumbers.Read(ref reader);
            }
            throw new JsonException("Unterminated quaternion array.");
        }

        public override void Write(Utf8JsonWriter writer, QuaternionD value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            JsonNumbers.Write(writer, value.X);
            JsonNumbers.Write(writer, value.Y);
            JsonNumbers.Write(writer, value.Z);
            JsonNumbers.Write(writer, value.W);
            writer.WriteEndArray();
        }
    }

    public static class JsonUtil
    {
        /// <summary>Options used for all game data and save files.</summary>
        public static readonly JsonSerializerOptions Options = Create(indented: true);

        public static JsonSerializerOptions Create(bool indented)
        {
            var o = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                WriteIndented = indented,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            };
            o.Converters.Add(new Vector3dConverter());
            o.Converters.Add(new QuaternionDConverter());
            o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
            return o;
        }
    }
}
