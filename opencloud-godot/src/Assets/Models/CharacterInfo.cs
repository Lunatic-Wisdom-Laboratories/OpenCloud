using System;
using System.Collections.Generic;
using OpenCloud.Core.Scripts;

namespace OpenCloud.Assets.Models;

/// <summary>One <c>KEY</c> interval of a motion set (<c>CHRINFO_KEY_SET</c>): a named action.</summary>
public sealed record MotionKeyInterval(string Name, int StartFrame, int EndFrame, float Step);

/// <summary>
/// One <c>MOTION</c> slot: the key file that poses the frames, the bind-pose matrices that
/// replace the frames' base matrices, and the slot's named actions.
/// </summary>
public sealed class MotionSet
{
    public required int Id { get; init; }
    public required string? MotionFile { get; init; }
    public required string? BindPoseFile { get; init; }

    /// <summary>
    /// Fourth argument: unused by <c>_MOTION</c>, but a costume read with the skin tags
    /// (<c>_SKIN_MOTION</c>) takes its weights from it.
    /// </summary>
    public string? WeightFile { get; init; }
    public List<MotionKeyInterval> Keys { get; } = new();
}

/// <summary>
/// The parts of a character pack's <c>info.cfg</c> (character <c>ScanInfoFile</c>) that build its model and animation: the model, the frames whose
/// visuals are skinned, and the motion slots with their <c>KEY</c> intervals. Other commands
/// (effects, sounds, cloth, outlines...) are kept in <see cref="Commands"/> for later stages.
/// </summary>
public sealed class CharacterInfo
{
    /// <summary>Retail name of the info script inside a character pack.</summary>
    public const string FileName = "info.cfg";

    /// <summary>The character tag table in retail order; binary scripts index it.</summary>
    public static readonly IReadOnlyList<string> Tags = new[]
    {
        "V2", "NAME", "IMG", "IMG_END", "BODY_SIZE", "SCALE", "MATERIAL_ANIME", "POLY_NUM",
        "MODEL", "SHADOW_MODEL", "OBJECT_NAME", "OBJECT_NAME2", "MOTION", "SHADOW_MOTION",
        "OUTLINE", "VERTEX_ANIME", "SHAPE_ANIME", "KEY_START", "KEY", "KEY_END", "SEQ_START",
        "SEQ", "SEQ_END", "CLOTH_START", "CLOTH", "CLOTH_END", "POSITION", "ROTATION",
        "SE_START", "SE", "SELP", "SE_END", "MOTION_END", "EFFECT_START", "EFFECT",
        "EFFECT_END", "LOD_MODEL_START", "LOD_MODEL", "LOD_MODEL_END",
    };

    /// <summary>Motion slots a character holds (<c>CCharacter2::motion[8]</c>).</summary>
    public const int MotionSetMax = 8;

    public required IReadOnlyList<ScriptCommand> Commands { get; init; }
    public string? Name { get; private set; }
    public string? Model { get; private set; }
    public string? ShadowModel { get; private set; }

    /// <summary>
    /// Frames named by <c>VERTEX_ANIME</c>: only their visuals follow the skeleton by weights
    /// (<c>_MODEL</c> creates motion visuals for them); every other visual moves rigidly with its frame.
    /// </summary>
    public List<string> SkinnedFrames { get; } = new();

    /// <summary>Nonzero <c>SHAPE_ANIME</c>: the skinned frames are deformed on the CPU instead.</summary>
    public bool ShapeAnime { get; private set; }

    public SortedDictionary<int, MotionSet> Motions { get; } = new();

    public static CharacterInfo Parse(ReadOnlySpan<byte> data)
    {
        var commands = GameScript.Parse(data, Tags);
        var info = new CharacterInfo { Commands = commands };
        int motionId = 0;
        bool inKeys = false;

        foreach (var command in commands)
        {
            switch (command.Tag)
            {
                case "NAME":
                    info.Name = command.Arg(0).AsString;
                    break;
                case "MODEL":
                    info.Model ??= command.Arg(0).AsString;
                    break;
                case "SHADOW_MODEL":
                    info.ShadowModel ??= command.Arg(0).AsString;
                    break;
                case "VERTEX_ANIME":
                    foreach (var arg in command.Args)
                    {
                        if (arg.AsString is { Length: > 0 } frame)
                            info.SkinnedFrames.Add(frame);
                    }
                    break;
                case "SHAPE_ANIME":
                    info.ShapeAnime = command.Arg(0).AsInt != 0;
                    break;
                case "MOTION":
                    // _MOTION: slot, key file, bind-pose file (a third name is read but unused).
                    motionId = command.Arg(0).AsInt;
                    if (motionId is < 0 or >= MotionSetMax)
                        break;
                    info.Motions[motionId] = new MotionSet
                    {
                        Id = motionId,
                        MotionFile = NonEmpty(command.Arg(1).AsString),
                        BindPoseFile = NonEmpty(command.Arg(2).AsString),
                        WeightFile = NonEmpty(command.Arg(3).AsString),
                    };
                    break;
                case "KEY_START":
                    inKeys = true;
                    if (info.Motions.TryGetValue(motionId, out var restarted))
                        restarted.Keys.Clear();
                    break;
                case "KEY_END":
                    inKeys = false;
                    break;
                case "KEY" when inKeys && command.Args.Count >= 4:
                    if (!info.Motions.TryGetValue(motionId, out var set))
                        info.Motions[motionId] = set = new MotionSet { Id = motionId, MotionFile = null, BindPoseFile = null };
                    set.Keys.Add(new MotionKeyInterval(
                        command.Arg(0).AsString ?? string.Empty,
                        command.Arg(1).AsInt,
                        command.Arg(2).AsInt,
                        command.Arg(3).AsFloat));
                    break;
            }
        }
        return info;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
