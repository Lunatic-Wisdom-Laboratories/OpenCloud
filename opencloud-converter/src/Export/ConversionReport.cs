using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenCloud.Core;

namespace OpenCloud.Converter.Export;

/// <summary>What a conversion produced, skipped and could not convert.</summary>
public sealed class ConversionReport
{
    public const string FileName = OpenCloud.Core.IO.VirtualFileSystem.ConvertedDataMarker;
    public const string Format = "opencloud.data";
    public const int Version = 1;

    public int Models;
    public int Characters;
    public int Costumes;
    public int TextureFiles;
    public int Maps;
    public int Skies;

    /// <summary>Original files of kinds the converter does not handle yet, by extension.</summary>
    public SortedDictionary<string, int> NotConverted { get; } = new(System.StringComparer.Ordinal);

    public List<string> Notes { get; } = new();
    public List<string> Failures { get; } = new();

    public void Note(string path, string message) => Notes.Add($"{path}: {message}");

    public void Fail(string path, string message) => Failures.Add($"{path}: {message}");

    public void Skip(string extension) => NotConverted[extension] = NotConverted.GetValueOrDefault(extension) + 1;

    /// <summary>The data folder's marker file: which converter wrote it and what it holds.</summary>
    public JsonObject ToJson(IEnumerable<string> sources) => new()
    {
        ["format"] = Format,
        ["version"] = Version,
        ["sources"] = JsonValues.Array(sources),
        ["models"] = Models,
        ["characters"] = Characters,
        ["costumes"] = Costumes,
        ["texture_files"] = TextureFiles,
        ["maps"] = Maps,
        ["skies"] = Skies,
        ["not_converted"] = new JsonObject(NotConverted.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value))),
        ["failures"] = JsonValues.Array(Failures),
    };
}
