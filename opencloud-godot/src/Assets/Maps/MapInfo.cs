using System;
using System.Collections.Generic;
using System.Numerics;
using OpenCloud.Core.Scripts;

namespace OpenCloud.Assets.Maps;

/// <summary>A directional light of a lighting set: direction towards the light, colour where 128 is 1.0.</summary>
public readonly record struct MapLight(Vector3 Direction, Vector3 Colour);

/// <summary>A point light of a lighting set (<c>mgPOINT_LIGHT</c>).</summary>
public readonly record struct MapPointLight(Vector3 Position, Vector3 Colour, float Power);

/// <summary>
/// One <c>LIGHT_SET n ... LIGHT_SET_END</c> block (<c>CMapLightingInfo</c>):
/// background colours, ambient, up to four directional and four point lights, and depth fog.
/// Colours keep the script's scale (background 0-255; ambient and lights 128 = 1.0).
/// </summary>
public sealed class MapLightingSet
{
    public const int MaxLights = 4;

    public int Index { get; init; }
    public bool HasFov { get; set; }
    public Vector3 BackgroundColour { get; set; }
    public Vector3 BackgroundColour2 { get; set; }
    public Vector3 Ambient { get; set; }
    public MapLight?[] Lights { get; } = new MapLight?[MaxLights];
    public MapPointLight?[] PointLights { get; } = new MapPointLight?[MaxLights];
    public bool FogEnable { get; set; }
    public float FogNear { get; set; }
    public float FogFar { get; set; }
    public Vector3 FogColour { get; set; } = new(255, 255, 255);

    /// <summary>Fog amount at the far distance, 0 (all fog) to 255 (none); <c>mgFOG_PARAM::far_value</c>.</summary>
    public float FogFarValue { get; set; }

    /// <summary>Fog amount at the near distance; <c>mgFOG_PARAM::near_value</c>.</summary>
    public float FogNearValue { get; set; } = 255;
}

/// <summary>
/// The map information read from a map's <c>.map</c> script by <c>CMapInfo::LoadMapInfo</c>:
/// the texture and model packs to load, the lighting sets and
/// the time-of-day settings. The same script is read a second time with the layout tags
/// (<see cref="MapLayout"/>).
/// </summary>
public sealed class MapInfo
{
    public const int MaxPacks = 16;
    public const int MaxLightingSets = 16;

    /// <summary><c>mapinfo_tag</c> in retail order; binary scripts index it.</summary>
    public static readonly IReadOnlyList<string> Tags = new[]
    {
        "IMG", "PCP", "ACTIVE_LIGHT_SET", "LIGHT_SET", "FOV", "BGCOLOR", "BGCOLOR2", "AMBIENT", "LIGHT",
        "PLIGHT", "FOG_ENABLE", "FOG", "LIGHT_SET_END", "FLOOR", "CHARA_POS", "TIME_FLAG",
        "TIME_LIGHT_NUM", "DEF_FOOT", "SKY_INFO", "LENS_FLARE", "TIME_CFADE", "ALL_SCISSOR",
        "CHARA_LIGHT_ADJUST",
    };

    /// <summary>IMG files of the map's <c>.ipk</c>, entered into the texture manager in this order.</summary>
    public List<string> TexturePacks { get; } = new();

    /// <summary>PCP model lists of the map's <c>.mpk</c>.</summary>
    public List<string> ModelPacks { get; } = new();

    public int ActiveLightSet { get; private set; }
    public MapLightingSet?[] LightingSets { get; } = new MapLightingSet?[MaxLightingSets];
    public float Floor { get; private set; }
    public Vector3 CharaPosition { get; private set; }

    /// <summary>TIME_FLAG: the map follows the game clock.</summary>
    public bool TimeEnable { get; private set; }

    /// <summary>TIME_FLAG: lighting sets are blended over the day and light 0 follows the sun.</summary>
    public bool TimeLightBlend { get; private set; }

    public float FixedTime { get; private set; } = 12.0f;
    public bool FixedTimeEnable { get; private set; }
    public int TimeLightCount { get; private set; } = 4;
    public int DefaultFoot { get; private set; }

    /// <summary>SKY_INFO: non-zero when the map draws a sky pack.</summary>
    public int SkyInfo { get; private set; }

    /// <summary>SKY_INFO second argument; meaning unresolved.</summary>
    public float SkyValue { get; private set; }

    /// <summary>SKY_INFO third argument in radians: the Y rotation of the sun's path.</summary>
    public float SunAngle { get; private set; }

    public int LensFlare { get; private set; } = 1;
    public int AllScissor { get; private set; }

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

    /// <summary>
    /// <c>CMap::GetNowTime</c>: the game clock when the map follows it, else the fixed time, else noon.
    /// </summary>
    public float NowTime(float clock) => TimeEnable ? clock : FixedTimeEnable ? FixedTime : 12.0f;

    /// <summary><c>GetTimeBand</c>: 0 day [9,17), 1 evening [17,21), 2 night, 3 morning [6,9).</summary>
    public static MapTimeBand TimeBand(float hour) => hour switch
    {
        >= 6.0f and < 9.0f => MapTimeBand.Morning,
        >= 9.0f and < 17.0f => MapTimeBand.Day,
        >= 17.0f and < 21.0f => MapTimeBand.Evening,
        _ => MapTimeBand.Night,
    };

    /// <summary><c>CMap::GetNowTimeLightBand</c>: which of the time lighting sets applies.</summary>
    public int TimeLightBand(float clock)
    {
        if (TimeLightCount < 2)
            return 0;
        float hour = NowTime(clock);
        if (TimeLightCount == 4)
            return (int)TimeBand(hour);
        hour -= 9.0f;
        if (hour < 0)
            hour += 24.0f;
        return (int)(hour / (24.0f / TimeLightCount)) % TimeLightCount;
    }

    /// <summary>
    /// The lighting set <c>CMap::GetLightInfo</c> starts from: the active set when the map has
    /// no clock (or the active set lies past the time sets), else the set of the current band.
    /// The blend towards the next band and the sun-following light 0 are not applied yet.
    /// </summary>
    public MapLightingSet? SelectLighting(float clock)
    {
        if (ActiveLightSet >= TimeLightCount || (!TimeEnable && !FixedTimeEnable))
            return Lighting(ActiveLightSet);
        return Lighting(TimeLightBand(clock)) ?? Lighting(ActiveLightSet);
    }

    private MapLightingSet? Lighting(int index) => index is >= 0 and < MaxLightingSets ? LightingSets[index] : null;
}

/// <summary>Time bands of the day (<c>MAP_TIME_BAND</c>), the index of the sky's per-band tables.</summary>
public enum MapTimeBand
{
    Day = 0,
    Evening = 1,
    Night = 2,
    Morning = 3,
}
