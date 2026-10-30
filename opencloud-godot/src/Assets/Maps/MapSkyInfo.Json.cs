using System.Linq;
using System.Text.Json.Nodes;
using static OpenCloud.Core.JsonValues;

namespace OpenCloud.Assets.Maps;

public sealed partial class MapSkyInfo
{
    private static JsonArray Names(string?[] names) => new(names.Select(n => (JsonNode?)n).ToArray());

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["images"] = Names(Images),
            ["skies"] = Names(Skies),
            ["sky_speeds"] = Array(SkySpeeds),
            ["suns"] = Names(Suns),
            ["back_skies"] = Names(BackSkies),
            ["back_sky_speeds"] = Array(BackSkySpeeds),
            ["animations"] = new JsonArray(Animations.Select(a => (JsonNode?)new JsonObject
            {
                ["band"] = a.Band,
                ["frame"] = a.Frame,
                ["speed"] = a.Speed,
                ["back"] = a.Back,
            }).ToArray()),
        };
        if (Background != null)
            json["background"] = Background;
        return json;
    }

    public static MapSkyInfo FromJson(JsonObject json)
    {
        var info = new MapSkyInfo { Background = Str(json, "background") };
        CopyNames(info.Images, json["images"]);
        CopyNames(info.Skies, json["skies"]);
        CopyNames(info.Suns, json["suns"]);
        CopyNames(info.BackSkies, json["back_skies"]);
        Floats(json["sky_speeds"]).Take(Bands).ToArray().CopyTo(info.SkySpeeds, 0);
        Floats(json["back_sky_speeds"]).Take(Bands).ToArray().CopyTo(info.BackSkySpeeds, 0);
        foreach (var a in Objects(json, "animations"))
            info.Animations.Add(new MapSkyAnimation(Int(a, "band"), Str(a, "frame") ?? string.Empty, Float(a, "speed"), Bool(a, "back")));
        return info;
    }

    private static void CopyNames(string?[] target, JsonNode? node)
    {
        if (node is not JsonArray array)
            return;
        for (int i = 0; i < target.Length && i < array.Count; i++)
            target[i] = array[i] is JsonValue value && value.TryGetValue(out string? name) ? name : null;
    }
}
