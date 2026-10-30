using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenCloud.Core;
using static OpenCloud.Core.JsonValues;

namespace OpenCloud.Assets.Maps;

/// <summary>
/// A converted map (<c>&lt;stem&gt;.map.json</c>): the map information and layout of its script,
/// the folders of its converted model lists (each with a <see cref="MapModelListDocument"/>) and
/// its sky (<see cref="MapSkyDocument"/>). Paths are relative to the data root.
/// </summary>
public sealed class MapDocument
{
    public const string Format = "opencloud.map";
    public const int Version = 1;
    public const string Extension = ".map.json";

    public required MapInfo Info { get; init; }
    public required MapLayout Layout { get; init; }

    /// <summary>Model list documents in the order the map script names them.</summary>
    public List<string> ModelLists { get; } = new();

    /// <summary>Sky document, or null when the map has no sky.</summary>
    public string? Sky { get; set; }

    /// <summary><c>map/s/s01/s01.map</c> → <c>map/s/s01/s01.map.json</c>.</summary>
    public static string PathFor(string mapPath) => mapPath + ".json";

    public JsonObject ToJson()
    {
        var json = Header(Format, Version);
        json["info"] = Info.ToJson();
        json["layout"] = Layout.ToJson();
        json["model_lists"] = Array(ModelLists);
        if (Sky != null)
            json["sky"] = Sky;
        return json;
    }

    public static MapDocument FromJson(JsonObject json, string path)
    {
        CheckFormat(json, Format, Version, path);
        var document = new MapDocument
        {
            Info = MapInfo.FromJson(json["info"] as JsonObject ?? new JsonObject()),
            Layout = MapLayout.FromJson(json["layout"] as JsonObject ?? new JsonObject()),
            Sky = Str(json, "sky"),
        };
        document.ModelLists.AddRange(Strings(json["model_lists"]));
        return document;
    }
}

/// <summary>What a model-list entry is loaded as (<c>CMdsInfo::type</c>).</summary>
public enum MapModelType
{
    Model = 0,
    Collision = 1,
    CameraCollision = 3,
    Character = 4,
}

/// <summary>One <c>MDS ... MDS_END</c> entry of a model list, with its converted file.</summary>
/// <param name="Name">Name pieces look the model up by.</param>
/// <param name="File">Converted GLB, relative to the list document.</param>
public sealed record MapModelEntry(string Name, MapModelType Type, float FarDistance, int FarFade, string File)
{
    /// <summary>Bit 0 of the type: collision models are never drawn (<c>CMapPiece::DrawSub</c>).</summary>
    public bool IsDrawn => ((int)Type & 1) == 0;
}

/// <summary>
/// A converted PCP model list (<c>&lt;list&gt;/list.json</c>): its entries in slot order, each a
/// GLB beside the document. Map pieces find their model by name across all of a map's lists.
/// </summary>
public sealed class MapModelListDocument
{
    public const string Format = "opencloud.model_list";
    public const int Version = 1;
    public const string FileName = "list.json";

    public required string Name { get; init; }
    public List<MapModelEntry> Entries { get; } = new();

    public MapModelEntry? Find(string name) =>
        Entries.Find(e => string.Equals(e.Name, name, System.StringComparison.OrdinalIgnoreCase));

    public JsonObject ToJson()
    {
        var json = Header(Format, Version);
        json["name"] = Name;
        json["models"] = new JsonArray(Entries.Select(e => (JsonNode?)new JsonObject
        {
            ["name"] = e.Name,
            ["type"] = (int)e.Type,
            ["far_distance"] = e.FarDistance,
            ["far_fade"] = e.FarFade,
            ["file"] = e.File,
        }).ToArray());
        return json;
    }

    public static MapModelListDocument FromJson(JsonObject json, string path)
    {
        CheckFormat(json, Format, Version, path);
        var document = new MapModelListDocument { Name = Str(json, "name") ?? string.Empty };
        foreach (var entry in Objects(json, "models"))
        {
            document.Entries.Add(new MapModelEntry(
                Str(entry, "name") ?? string.Empty,
                (MapModelType)Int(entry, "type"),
                Float(entry, "far_distance", -1.0f),
                Int(entry, "far_fade"),
                Str(entry, "file") ?? string.Empty));
        }
        return document;
    }
}

/// <summary>
/// A converted sky pack (<c>&lt;pack&gt;/sky.json</c>): the sky information, and per time band
/// the converted sky and back-sky models (each a GLB beside the document, textured with that
/// band's images).
/// </summary>
public sealed class MapSkyDocument
{
    public const string Format = "opencloud.sky";
    public const int Version = 1;
    public const string FileName = "sky.json";

    public required MapSkyInfo Info { get; init; }

    /// <summary>Converted sky model of each band, or null.</summary>
    public string?[] SkyFiles { get; } = new string?[MapSkyInfo.Bands];

    /// <summary>Converted back-sky model of each band, or null.</summary>
    public string?[] BackSkyFiles { get; } = new string?[MapSkyInfo.Bands];

    public JsonObject ToJson()
    {
        var json = Header(Format, Version);
        json["info"] = Info.ToJson();
        json["sky_files"] = new JsonArray(SkyFiles.Select(f => (JsonNode?)f).ToArray());
        json["back_sky_files"] = new JsonArray(BackSkyFiles.Select(f => (JsonNode?)f).ToArray());
        return json;
    }

    public static MapSkyDocument FromJson(JsonObject json, string path)
    {
        CheckFormat(json, Format, Version, path);
        var document = new MapSkyDocument { Info = MapSkyInfo.FromJson(json["info"] as JsonObject ?? new JsonObject()) };
        Fill(document.SkyFiles, json["sky_files"]);
        Fill(document.BackSkyFiles, json["back_sky_files"]);
        return document;
    }

    private static void Fill(string?[] target, JsonNode? node)
    {
        if (node is not JsonArray array)
            return;
        for (int i = 0; i < target.Length && i < array.Count; i++)
            target[i] = array[i] is JsonValue value && value.TryGetValue(out string? file) ? file : null;
    }
}
