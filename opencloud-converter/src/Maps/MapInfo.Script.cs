using System;
using System.Collections.Generic;
using System.Numerics;
using OpenCloud.Core.Scripts;

namespace OpenCloud.Assets.Maps;

/// <summary>Reads the map information tags of a retail <c>.map</c> script.</summary>
public sealed partial class MapInfo
{
    /// <summary><c>mapinfo_tag</c> in retail order; binary scripts index it.</summary>
    public static readonly IReadOnlyList<string> Tags = new[]
    {
        "IMG", "PCP", "ACTIVE_LIGHT_SET", "LIGHT_SET", "FOV", "BGCOLOR", "BGCOLOR2", "AMBIENT", "LIGHT",
        "PLIGHT", "FOG_ENABLE", "FOG", "LIGHT_SET_END", "FLOOR", "CHARA_POS", "TIME_FLAG",
        "TIME_LIGHT_NUM", "DEF_FOOT", "SKY_INFO", "LENS_FLARE", "TIME_CFADE", "ALL_SCISSOR",
        "CHARA_LIGHT_ADJUST",
    };

    public static MapInfo Parse(IEnumerable<ScriptCommand> commands)
    {
        var info = new MapInfo();
        MapLightingSet? set = null;
        foreach (var command in commands)
        {
            var args = command.Args;
            switch (command.Tag)
            {
                case "IMG" when args.Count > 0 && info.TexturePacks.Count < MaxPacks && command.Arg(0).AsString is { } image:
                    info.TexturePacks.Add(image);
                    break;
                case "PCP" when args.Count > 0 && info.ModelPacks.Count < MaxPacks && command.Arg(0).AsString is { } models:
                    info.ModelPacks.Add(models);
                    break;
                case "ACTIVE_LIGHT_SET":
                    info.ActiveLightSet = command.Arg(0).AsInt;
                    break;
                case "LIGHT_SET":
                {
                    int index = command.Arg(0).AsInt;
                    set = index is >= 0 and < MaxLightingSets
                        ? info.LightingSets[index] ??= new MapLightingSet { Index = index }
                        : null;
                    break;
                }
                case "LIGHT_SET_END":
                    set = null;
                    break;
                case "FLOOR":
                    info.Floor = command.Arg(0).AsFloat;
                    break;
                case "CHARA_POS":
                    info.CharaPosition = ReadVector(command, 0);
                    break;
                case "TIME_FLAG":
                    info.TimeEnable = command.Arg(0).AsInt != 0;
                    info.TimeLightBlend = command.Arg(1).AsInt != 0;
                    if (args.Count >= 3)
                        info.FixedTime = command.Arg(2).AsFloat;
                    if (args.Count >= 4)
                        info.FixedTimeEnable = command.Arg(3).AsInt != 0;
                    break;
                case "TIME_LIGHT_NUM":
                    info.TimeLightCount = command.Arg(0).AsInt;
                    break;
                case "DEF_FOOT":
                    info.DefaultFoot = command.Arg(0).AsInt;
                    break;
                case "SKY_INFO":
                    info.SkyInfo = command.Arg(0).AsInt;
                    info.SkyValue = command.Arg(1).AsFloat;
                    info.SunAngle = AngleLimit(command.Arg(2).AsFloat * MathF.PI / 180.0f);
                    break;
                case "LENS_FLARE":
                    info.LensFlare = command.Arg(0).AsInt;
                    break;
                case "ALL_SCISSOR":
                    info.AllScissor = command.Arg(0).AsInt;
                    break;
                default:
                    if (set != null)
                        ReadLighting(set, command);
                    break;
            }
        }

        // BGCOLOR2 falls back to BGCOLOR when it is black or missing.
        foreach (var lighting in info.LightingSets)
        {
            if (lighting != null && lighting.BackgroundColour2 == Vector3.Zero)
                lighting.BackgroundColour2 = lighting.BackgroundColour;
        }
        return info;
    }

    private static void ReadLighting(MapLightingSet set, ScriptCommand command)
    {
        var args = command.Args;
        switch (command.Tag)
        {
            case "FOV":
                set.HasFov = true;
                break;
            case "BGCOLOR":
                set.BackgroundColour = ReadVector(command, 0);
                break;
            case "BGCOLOR2":
                set.BackgroundColour2 = ReadVector(command, 0);
                break;
            case "AMBIENT":
                set.Ambient = ReadVector(command, 0);
                break;
            case "LIGHT":
            {
                int index = command.Arg(0).AsInt;
                if (index is < 0 or >= MapLightingSet.MaxLights || args.Count < 4)
                    break;
                var direction = ReadVector(command, 1);
                direction = direction.LengthSquared() > 0 ? Vector3.Normalize(direction) : direction;
                var colour = args.Count >= 7 ? ReadVector(command, 4) : set.Lights[index]?.Colour ?? Vector3.Zero;
                set.Lights[index] = new MapLight(direction, colour);
                break;
            }
            case "PLIGHT":
            {
                int index = command.Arg(0).AsInt;
                if (index is < 0 or >= MapLightingSet.MaxLights)
                    break;
                set.PointLights[index] = new MapPointLight(ReadVector(command, 2), ReadVector(command, 5), command.Arg(1).AsFloat);
                break;
            }
            case "FOG_ENABLE":
                set.FogEnable = command.Arg(0).AsInt != 0;
                break;
            case "FOG":
                set.FogNear = command.Arg(0).AsFloat;
                set.FogFar = command.Arg(1).AsFloat;
                set.FogColour = args.Count > 2
                    ? new Vector3(command.Arg(2).AsInt & 0xFF, command.Arg(3).AsInt & 0xFF, command.Arg(4).AsInt & 0xFF)
                    : new Vector3(255, 255, 255);
                set.FogFarValue = args.Count > 5 ? command.Arg(5).AsFloat : 0;
                set.FogNearValue = args.Count > 5 ? command.Arg(6).AsFloat : 255;
                break;
        }
    }

    /// <summary>Three consecutive floats, as <c>spiGetStackVector</c> reads them.</summary>
    internal static Vector3 ReadVector(ScriptCommand command, int first) =>
        new(command.Arg(first).AsFloat, command.Arg(first + 1).AsFloat, command.Arg(first + 2).AsFloat);

    /// <summary><c>mgAngleLimit</c>: wraps an angle into [-pi, pi].</summary>
    internal static float AngleLimit(float angle)
    {
        while (angle > MathF.PI)
            angle -= 2 * MathF.PI;
        while (angle < -MathF.PI)
            angle += 2 * MathF.PI;
        return angle;
    }

}
