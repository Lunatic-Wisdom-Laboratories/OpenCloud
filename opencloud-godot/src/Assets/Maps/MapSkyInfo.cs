using System;
using System.Collections.Generic;
using System.Linq;
using OpenCloud.Core.Scripts;

namespace OpenCloud.Assets.Maps;

/// <summary>A frame of a sky model that spins about Y (SKY_ANIME / SKYB_ANIME).</summary>
public sealed record MapSkyAnimation(int Band, string Frame, float Speed, bool Back);

/// <summary>
/// The <c>info.cfg</c> of a sky pack (<c>MAP_SKY_INFO</c>). Every
/// table has one entry per time band (<see cref="MapTimeBand"/>): the IMG with the band's
/// textures, the sky and back-sky models with their spin speeds and the sun (or moon) model.
/// Speeds are radians per frame.
/// </summary>
public sealed class MapSkyInfo
{
    public const int Bands = 4;
    public const int MaxAnimations = 16;

    /// <summary>The tag table of <c>LoadSkyPack</c> in retail order; binary scripts index it.</summary>
    public static readonly IReadOnlyList<string> Tags = new[]
    {
        "SKY_IMG", "SKY_MDS", "SKY_ANIME", "SUN_MDS", "SKYB_MDS", "SKYB_ANIME", "SKY_BG",
    };

    public string?[] Images { get; } = new string?[Bands];
    public string?[] Skies { get; } = new string?[Bands];
    public float[] SkySpeeds { get; } = new float[Bands];
    public string?[] Suns { get; } = new string?[Bands];
    public string?[] BackSkies { get; } = new string?[Bands];
    public float[] BackSkySpeeds { get; } = new float[Bands];
    public string? Background { get; private set; }
    public List<MapSkyAnimation> Animations { get; } = new();

    public static MapSkyInfo Parse(ReadOnlySpan<byte> script)
    {
        var info = new MapSkyInfo();
        int skyAnimations = 0, backAnimations = 0;
        foreach (var command in GameScript.Parse(script, Tags))
        {
            int band = command.Arg(0).AsInt;
            bool inRange = band is >= 0 and < Bands;
            string? name = command.Arg(1).AsString;
            float speed = command.Arg(2).AsFloat * MathF.PI / 180.0f;
            switch (command.Tag)
            {
                case "SKY_IMG" when inRange:
                    info.Images[band] = name;
                    break;
                case "SKY_MDS" when inRange:
                    info.Skies[band] = name;
                    info.SkySpeeds[band] = speed;
                    break;
                case "SUN_MDS" when inRange:
                    info.Suns[band] = name;
                    break;
                case "SKYB_MDS" when inRange:
                    info.BackSkies[band] = name;
                    info.BackSkySpeeds[band] = speed;
                    break;
                case "SKY_BG":
                    info.Background = command.Arg(0).AsString;
                    break;
                case "SKY_ANIME" when inRange && name != null && skyAnimations < MaxAnimations:
                    info.Animations.Add(new MapSkyAnimation(band, name, speed, false));
                    skyAnimations++;
                    break;
                case "SKYB_ANIME" when inRange && name != null && backAnimations < MaxAnimations:
                    info.Animations.Add(new MapSkyAnimation(band, name, speed, true));
                    backAnimations++;
                    break;
            }
        }
        // LoadPack takes the sky entries first and keeps 16 in all.
        var ordered = info.Animations.OrderBy(a => a.Back).Take(MaxAnimations).ToList();
        info.Animations.Clear();
        info.Animations.AddRange(ordered);
        return info;
    }
}
