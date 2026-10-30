using System.Linq;
using System.Text.Json.Nodes;
using static OpenCloud.Core.JsonValues;

namespace OpenCloud.Assets.Maps;

public sealed partial class MapInfo
{
    public JsonObject ToJson()
    {
        var sets = new JsonArray();
        foreach (var set in LightingSets)
        {
            if (set == null)
                continue;
            var lights = new JsonArray();
            for (int i = 0; i < set.Lights.Length; i++)
            {
                if (set.Lights[i] is { } light)
                    lights.Add(new JsonObject { ["index"] = i, ["direction"] = Array(light.Direction), ["colour"] = Array(light.Colour) });
            }
            var pointLights = new JsonArray();
            for (int i = 0; i < set.PointLights.Length; i++)
            {
                if (set.PointLights[i] is { } light)
                    pointLights.Add(new JsonObject { ["index"] = i, ["position"] = Array(light.Position), ["colour"] = Array(light.Colour), ["power"] = light.Power });
            }
            sets.Add(new JsonObject
            {
                ["index"] = set.Index,
                ["fov"] = set.HasFov,
                ["background_colour"] = Array(set.BackgroundColour),
                ["background_colour2"] = Array(set.BackgroundColour2),
                ["ambient"] = Array(set.Ambient),
                ["lights"] = lights,
                ["point_lights"] = pointLights,
                ["fog_enable"] = set.FogEnable,
                ["fog_near"] = set.FogNear,
                ["fog_far"] = set.FogFar,
                ["fog_colour"] = Array(set.FogColour),
                ["fog_far_value"] = set.FogFarValue,
                ["fog_near_value"] = set.FogNearValue,
            });
        }
        return new JsonObject
        {
            ["texture_packs"] = Array(TexturePacks),
            ["model_packs"] = Array(ModelPacks),
            ["active_light_set"] = ActiveLightSet,
            ["lighting_sets"] = sets,
            ["floor"] = Floor,
            ["chara_position"] = Array(CharaPosition),
            ["time_enable"] = TimeEnable,
            ["time_light_blend"] = TimeLightBlend,
            ["fixed_time"] = FixedTime,
            ["fixed_time_enable"] = FixedTimeEnable,
            ["time_light_count"] = TimeLightCount,
            ["default_foot"] = DefaultFoot,
            ["sky_info"] = SkyInfo,
            ["sky_value"] = SkyValue,
            ["sun_angle"] = SunAngle,
            ["lens_flare"] = LensFlare,
            ["all_scissor"] = AllScissor,
        };
    }

    public static MapInfo FromJson(JsonObject json)
    {
        var info = new MapInfo
        {
            ActiveLightSet = Int(json, "active_light_set"),
            Floor = Float(json, "floor"),
            CharaPosition = Vector3(json, "chara_position"),
            TimeEnable = Bool(json, "time_enable"),
            TimeLightBlend = Bool(json, "time_light_blend"),
            FixedTime = Float(json, "fixed_time", 12.0f),
            FixedTimeEnable = Bool(json, "fixed_time_enable"),
            TimeLightCount = Int(json, "time_light_count", 4),
            DefaultFoot = Int(json, "default_foot"),
            SkyInfo = Int(json, "sky_info"),
            SkyValue = Float(json, "sky_value"),
            SunAngle = Float(json, "sun_angle"),
            LensFlare = Int(json, "lens_flare", 1),
            AllScissor = Int(json, "all_scissor"),
        };
        info.TexturePacks.AddRange(Strings(json["texture_packs"]));
        info.ModelPacks.AddRange(Strings(json["model_packs"]));
        foreach (var setJson in Objects(json, "lighting_sets"))
        {
            int index = Int(setJson, "index");
            if (index is < 0 or >= MaxLightingSets)
                continue;
            var set = new MapLightingSet
            {
                Index = index,
                HasFov = Bool(setJson, "fov"),
                BackgroundColour = Vector3(setJson, "background_colour"),
                BackgroundColour2 = Vector3(setJson, "background_colour2"),
                Ambient = Vector3(setJson, "ambient"),
                FogEnable = Bool(setJson, "fog_enable"),
                FogNear = Float(setJson, "fog_near"),
                FogFar = Float(setJson, "fog_far"),
                FogColour = Vector3(setJson, "fog_colour", new System.Numerics.Vector3(255, 255, 255)),
                FogFarValue = Float(setJson, "fog_far_value"),
                FogNearValue = Float(setJson, "fog_near_value", 255),
            };
            foreach (var light in Objects(setJson, "lights"))
            {
                int i = Int(light, "index");
                if (i is >= 0 and < MapLightingSet.MaxLights)
                    set.Lights[i] = new MapLight(Vector3(light, "direction"), Vector3(light, "colour"));
            }
            foreach (var light in Objects(setJson, "point_lights"))
            {
                int i = Int(light, "index");
                if (i is >= 0 and < MapLightingSet.MaxLights)
                    set.PointLights[i] = new MapPointLight(Vector3(light, "position"), Vector3(light, "colour"), Float(light, "power"));
            }
            info.LightingSets[index] = set;
        }
        return info;
    }
}
