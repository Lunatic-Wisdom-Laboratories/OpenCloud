using System;
using System.Collections.Generic;

namespace OpenCloud.Assets.Maps;

/// <summary>A frame of a sky model that spins about Y (SKY_ANIME / SKYB_ANIME).</summary>
public sealed record MapSkyAnimation(int Band, string Frame, float Speed, bool Back);

/// <summary>
/// The <c>info.cfg</c> of a sky pack (<c>MAP_SKY_INFO</c>). Every
/// table has one entry per time band (<see cref="MapTimeBand"/>): the IMG with the band's
/// textures, the sky and back-sky models with their spin speeds and the sun (or moon) model.
/// Speeds are radians per frame.
/// </summary>
public sealed partial class MapSkyInfo
{
    public const int Bands = 4;
    public const int MaxAnimations = 16;

    public string?[] Images { get; } = new string?[Bands];
    public string?[] Skies { get; } = new string?[Bands];
    public float[] SkySpeeds { get; } = new float[Bands];
    public string?[] Suns { get; } = new string?[Bands];
    public string?[] BackSkies { get; } = new string?[Bands];
    public float[] BackSkySpeeds { get; } = new float[Bands];
    public string? Background { get; private set; }
    public List<MapSkyAnimation> Animations { get; } = new();


}
