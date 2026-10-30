using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace OpenCloud.Core;

/// <summary>Reading and writing the values of the converted data's JSON documents.</summary>
public static class JsonValues
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static JsonObject Parse(byte[] json, string path) =>
        JsonNode.Parse(json) as JsonObject ?? throw new System.IO.InvalidDataException($"{path} is not a JSON object.");

    /// <summary>Checks the <c>format</c> and <c>version</c> fields every document starts with.</summary>
    public static void CheckFormat(JsonObject json, string format, int version, string path)
    {
        string? actual = Str(json, "format");
        if (actual != format)
            throw new System.IO.InvalidDataException($"{path}: format '{actual}' is not '{format}'.");
        int actualVersion = Int(json, "version");
        if (actualVersion > version)
            throw new System.IO.InvalidDataException($"{path}: version {actualVersion} is newer than this engine reads ({version}); convert the data again.");
    }

    public static JsonObject Header(string format, int version) => new() { ["format"] = format, ["version"] = version };

    public static JsonArray Array(NVector3 v) => new(v.X, v.Y, v.Z);

    public static JsonArray Array(NVector4 v) => new(v.X, v.Y, v.Z, v.W);

    public static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)v).ToArray());

    public static JsonArray Array(IEnumerable<float> values) => new(values.Select(v => (JsonNode?)v).ToArray());

    /// <summary>A transform as 12 numbers: the three basis columns, then the origin.</summary>
    public static JsonArray Array(Transform3D t) => new(
        t.Basis.Column0.X, t.Basis.Column0.Y, t.Basis.Column0.Z,
        t.Basis.Column1.X, t.Basis.Column1.Y, t.Basis.Column1.Z,
        t.Basis.Column2.X, t.Basis.Column2.Y, t.Basis.Column2.Z,
        t.Origin.X, t.Origin.Y, t.Origin.Z);

    public static Transform3D Transform(JsonNode? node)
    {
        var f = Floats(node);
        if (f.Length != 12)
            throw new System.IO.InvalidDataException("A transform needs 12 numbers.");
        return new Transform3D(new Vector3(f[0], f[1], f[2]), new Vector3(f[3], f[4], f[5]), new Vector3(f[6], f[7], f[8]), new Vector3(f[9], f[10], f[11]));
    }

    public static float[] Floats(JsonNode? node) => node is JsonArray array ? array.Select(v => v?.GetValue<float>() ?? 0).ToArray() : System.Array.Empty<float>();

    public static string[] Strings(JsonNode? node) => node is JsonArray array ? array.Select(v => v?.GetValue<string>() ?? string.Empty).ToArray() : System.Array.Empty<string>();

    public static NVector3 Vector3(JsonObject json, string key, NVector3 fallback = default)
    {
        var f = Floats(json[key]);
        return f.Length >= 3 ? new NVector3(f[0], f[1], f[2]) : fallback;
    }

    public static NVector4 Vector4(JsonObject json, string key)
    {
        var f = Floats(json[key]);
        return f.Length >= 4 ? new NVector4(f[0], f[1], f[2], f[3]) : default;
    }

    public static string? Str(JsonObject json, string key) => json[key] is JsonValue value && value.TryGetValue(out string? s) ? s : null;

    public static int Int(JsonObject json, string key, int fallback = 0) => json[key] is JsonValue value && value.TryGetValue(out int i) ? i : fallback;

    public static float Float(JsonObject json, string key, float fallback = 0) => json[key] is JsonValue value && value.TryGetValue(out float f) ? f : fallback;

    public static bool Bool(JsonObject json, string key, bool fallback = false) => json[key] is JsonValue value && value.TryGetValue(out bool b) ? b : fallback;

    public static IEnumerable<JsonObject> Objects(JsonObject json, string key) =>
        json[key] is JsonArray array ? array.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();
}
