using System;
using System.Collections.Generic;
using System.Numerics;

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
public sealed partial class MapInfo
{
    public const int MaxPacks = 16;
    public const int MaxLightingSets = 16;

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
