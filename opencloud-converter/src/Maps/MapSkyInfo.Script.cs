using System;
using System.Collections.Generic;
using System.Linq;
using OpenCloud.Core.Scripts;

namespace OpenCloud.Assets.Maps;

/// <summary>Reads the <c>info.cfg</c> script of a retail sky pack.</summary>
public sealed partial class MapSkyInfo
{
    /// <summary>The tag table of <c>LoadSkyPack</c> in retail order; binary scripts index it.</summary>
    public static readonly IReadOnlyList<string> Tags = new[]
    {
        "SKY_IMG", "SKY_MDS", "SKY_ANIME", "SUN_MDS", "SKYB_MDS", "SKYB_ANIME", "SKY_BG",
    };

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
    }}
