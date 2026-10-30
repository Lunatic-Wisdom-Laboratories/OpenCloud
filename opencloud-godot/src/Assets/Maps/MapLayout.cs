using System;
using System.Collections.Generic;
using System.Numerics;
using OpenCloud.Core.Scripts;

namespace OpenCloud.Assets.Maps;

/// <summary>A material colour override of a piece (<c>PieceMaterial</c>): frame, material slot, colour.</summary>
public sealed record MapPieceMaterial(string Frame, int MaterialIndex, Vector4 Colour, int ColourIndex);

/// <summary>
/// One <c>PIECE ... PIECE_END</c> of a part definition (<c>CMapPiece</c>):
/// a model of the map's model lists placed inside the part.
/// </summary>
public sealed class MapPieceDefinition
{
    /// <summary>Model looked up in the model lists when the piece is created (<c>CMap::SearchMDS</c>).</summary>
    public required string Model { get; init; }

    /// <summary>Piece name; PIECE_NAME renames it without changing the model.</summary>
    public required string Name { get; set; }

    public bool Show { get; init; } = true;
    public Vector3 Position { get; set; }

    /// <summary>Euler angles in radians, applied X, then Y, then Z (<c>mgCFrame::GetLocalMatrix</c>).</summary>
    public Vector3 Rotation { get; set; }

    public Vector3 Scale { get; set; } = Vector3.One;

    /// <summary>PIECE_COL_TYPE: non-zero pieces are not copied into placed parts (<c>CMapParts::Copy</c>).</summary>
    public int CollisionType { get; set; }

    public int CollisionParam { get; set; }

    /// <summary>PIECE_TIME: the piece is drawn while <c>CheckTime(now, start, end)</c> holds.</summary>
    public float TimeStart { get; set; }

    public float TimeEnd { get; set; }
    public List<MapPieceMaterial> Materials { get; } = new();

    /// <summary><c>CheckTime</c>: inside [start, end), wrapping past midnight when end is before start.</summary>
    public bool IsActiveAt(float hour)
    {
        if (TimeEnd > TimeStart)
            return hour >= TimeStart && hour < TimeEnd;
        if (TimeStart > TimeEnd)
            return hour >= TimeStart || hour < TimeEnd;
        return true;
    }
}

/// <summary>Kinds of function points (<c>FUNC_POINT_TYPE</c>).</summary>
public enum MapFuncPointType
{
    None = 0,
    Effect = 1,
    Fire = 2,
    Flare = 3,
    PointLight = 4,
    Anime = 5,
    Event = 6,
    Invent = 7,
    Sound = 8,
    Position = 9,
}

/// <summary>
/// A function point (<c>CFuncPoint</c>): a named marker for effects, lights, events, sounds...
/// Only what places it is kept for now; the per-kind data rows are left for later stages.
/// </summary>
public sealed class MapFuncPoint
{
    public required MapFuncPointType Type { get; init; }
    public bool Enable { get; init; }
    public string? Name { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public Vector3 Scale { get; set; } = Vector3.One;
}

/// <summary>A <c>PARTS ... PARTS_END</c> block: a reusable group of pieces (<c>CMapParts</c>).</summary>
public sealed class MapPartsDefinition
{
    public required string Name { get; init; }
    public float FarDistance { get; set; } = -1.0f;
    public int FarFade { get; set; }

    /// <summary>LIGHT_FLAG: drawn without the map's directional lights (ambient only).</summary>
    public bool NoLight { get; set; }

    /// <summary>LIGHT_FLAG: drawn without point lights.</summary>
    public bool NoPointLight { get; set; }

    /// <summary>MOVE_FLAG: the four wall bits the dungeon generator uses.</summary>
    public int MoveFlags { get; set; }

    public List<MapPieceDefinition> Pieces { get; } = new();
    public List<MapFuncPoint> FuncPoints { get; } = new();
}

/// <summary>A <c>MAP_PARTS ... MAP_PARTS_END</c> block: one placement of a part definition.</summary>
public sealed class MapPlacement
{
    public required string Name { get; init; }
    public bool Show { get; init; } = true;

    /// <summary>PARTS_NAME: the part definition placed here; empty places nothing.</summary>
    public string Definition { get; set; } = string.Empty;

    public string Group { get; set; } = string.Empty;
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public Vector3 Scale { get; set; } = Vector3.One;
    public float FarDistance { get; set; } = -1.0f;
    public int FarFade { get; set; }
}

/// <summary>
/// The layout read from a map's <c>.map</c> script by <c>CMap::LoadMapFile</c> (<c>map_tag</c>):
/// part definitions with their pieces, the placements of those parts, and
/// the map's own function points. Camera tables (CAMERA_INFO) are not read yet.
/// </summary>
public sealed class MapLayout
{
    /// <summary>
    /// <c>map_tag</c> in retail order; binary scripts index it. Its first 35 entries are
    /// placeholders that keep the map information tags out of this table.
    /// </summary>
    public static readonly IReadOnlyList<string> Tags = BuildTags();

    public List<MapPartsDefinition> Parts { get; } = new();
    public List<MapPlacement> Placements { get; } = new();
    public List<MapFuncPoint> FuncPoints { get; } = new();

    /// <summary>The first definition of the name, as <c>CMap::GetParts</c> walks its list.</summary>
    public MapPartsDefinition? FindParts(string name) =>
        Parts.Find(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    private static IReadOnlyList<string> BuildTags()
    {
        var tags = new List<string>();
        for (int i = 0; i < 35; i++)
            tags.Add("d");
        tags.AddRange(new[]
        {
            "PARTS", "FAR_CLIP", "LIGHT_FLAG", "MOVE_FLAG", "LOD_START", "LOD_BLEND", "LOD_PIECE", "LOD_END",
            "PIECE", "PIECE_POS", "PIECE_NAME", "PIECE_ROT", "PIECE_SCALE", "PIECE_MATERIAL_START",
            "PIECE_MATERIAL", "PIECE_MATERIAL_END", "PIECE_COL_TYPE", "PIECE_TIME", "PIECE_END", "PARTS_END",
            "MAP_PARTS", "MAP_FAR_CLIP", "PARTS_NAME", "PARTS_GROUP", "PARTS_POS", "PARTS_ROT", "PARTS_SCALE",
            "MAP_PARTS_END", "_MAP_INFO_TOP", "CAMERA_INFO", "FIX_CAMERA", "FIX_CAMERA_POS", "FIX_CAMERA_POS2",
            "FIX_CAMERA_OFF_GROUP", "FIX_CAMERA_RECT", "FIX_CAMERA_END", "CAMERA_INFO_END", "FUNC_POINT",
            "FUNC_DATA", "FUNC_NAME", "FUNC_FLAG", "FUNC_FIRE_DATA", "FUNC_EFFECT_NAME", "FUNC_FIRE_DATA",
            "FUNC_PLIGHT_DATA", "FUNC_ANIME_DATA", "FUNC_INVENT_DATA", "FUNC_EVENT_DATA", "FUNC_POS",
            "FUNC_DATA_END", "FUNC_POINT_END", "FUNC_SOUND_DATA",
        });
        return tags;
    }

    /// <summary>
    /// Runs the layout tags in script order with the loader's "current" state: the part being
    /// defined, its current piece, the placement being configured and the current function point.
    /// </summary>
    public static MapLayout Parse(IEnumerable<ScriptCommand> commands)
    {
        var layout = new MapLayout();
        MapPartsDefinition? parts = null;
        MapPieceDefinition? piece = null;
        MapPlacement? placement = null;
        MapFuncPoint? point = null;
        bool partsPoints = false;

        foreach (var command in commands)
        {
            var args = command.Args;
            switch (command.Tag)
            {
                case "PARTS":
                    parts = new MapPartsDefinition { Name = command.Arg(0).AsString ?? string.Empty };
                    piece = null;
                    partsPoints = true;
                    break;
                case "FAR_CLIP" when parts != null:
                    parts.FarDistance = command.Arg(0).AsFloat;
                    parts.FarFade = command.Arg(1).AsInt;
                    break;
                case "LIGHT_FLAG" when parts != null:
                    parts.NoLight = command.Arg(0).AsInt != 0;
                    parts.NoPointLight = command.Arg(1).AsInt != 0;
                    break;
                case "MOVE_FLAG" when parts != null:
                    parts.MoveFlags = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        if (command.Arg(i).AsInt != 0)
                            parts.MoveFlags |= 1 << i;
                    }
                    break;
                case "PIECE" when parts != null && command.Arg(0).AsString is { } model:
                    piece = new MapPieceDefinition
                    {
                        Model = model,
                        Name = model,
                        Show = args.Count < 2 || command.Arg(1).AsInt != 0,
                    };
                    break;
                case "PIECE_NAME" when piece != null && command.Arg(0).AsString is { } name:
                    piece.Name = name;
                    break;
                case "PIECE_POS" when piece != null:
                    piece.Position = MapInfo.ReadVector(command, 0);
                    break;
                case "PIECE_ROT" when piece != null:
                    piece.Rotation = MapInfo.ReadVector(command, 0);
                    break;
                case "PIECE_SCALE" when piece != null:
                    piece.Scale = MapInfo.ReadVector(command, 0);
                    break;
                case "PIECE_MATERIAL" when piece != null && command.Arg(0).AsString is { } frame:
                    piece.Materials.Add(new MapPieceMaterial(frame, command.Arg(1).AsInt,
                        new Vector4(command.Arg(2).AsFloat, command.Arg(3).AsFloat, command.Arg(4).AsFloat, command.Arg(5).AsFloat),
                        command.Arg(6).AsInt));
                    break;
                case "PIECE_COL_TYPE" when piece != null:
                    piece.CollisionType = command.Arg(0).AsInt;
                    if (args.Count >= 2)
                        piece.CollisionParam = command.Arg(1).AsInt;
                    break;
                case "PIECE_TIME" when piece != null:
                    piece.TimeStart = command.Arg(0).AsFloat;
                    piece.TimeEnd = command.Arg(1).AsFloat;
                    break;
                case "PIECE_END" when parts != null && piece != null:
                    parts.Pieces.Add(piece);
                    break;
                case "PARTS_END" when parts != null:
                    layout.Parts.Add(parts);
                    partsPoints = false;
                    break;
                case "MAP_PARTS":
                    parts = null;
                    placement = command.Arg(0).AsString is { } placed
                        ? new MapPlacement { Name = placed, Show = args.Count <= 1 || command.Arg(1).AsInt != 0 }
                        : null;
                    break;
                case "MAP_FAR_CLIP" when placement != null:
                    placement.FarDistance = command.Arg(0).AsFloat;
                    placement.FarFade = command.Arg(1).AsInt;
                    break;
                case "PARTS_NAME" when placement != null && command.Arg(0).AsString is { } definition:
                    placement.Definition = definition;
                    break;
                case "PARTS_GROUP" when placement != null && command.Arg(0).AsString is { } group:
                    placement.Group = group;
                    break;
                case "PARTS_POS" when placement != null:
                    placement.Position = MapInfo.ReadVector(command, 0);
                    break;
                case "PARTS_ROT" when placement != null:
                    placement.Rotation = MapInfo.ReadVector(command, 0);
                    break;
                case "PARTS_SCALE" when placement != null:
                    placement.Scale = MapInfo.ReadVector(command, 0);
                    break;
                case "MAP_PARTS_END" when placement != null:
                    layout.Placements.Add(placement);
                    break;
                case "FUNC_DATA":
                    point = CreateFuncPoint(command);
                    if (point != null)
                    {
                        if (!partsPoints)
                            layout.FuncPoints.Add(point);
                        else if (parts != null)
                            parts.FuncPoints.Add(point);
                        else
                            point = null;
                    }
                    break;
                case "FUNC_NAME" when point != null && command.Arg(0).AsString is { } pointName:
                    point.Name = pointName;
                    break;
                case "FUNC_POS" when point != null:
                    point.Position = MapInfo.ReadVector(command, 0);
                    var rotation = MapInfo.ReadVector(command, 3);
                    point.Rotation = new Vector3(MapInfo.AngleLimit(rotation.X), MapInfo.AngleLimit(rotation.Y), MapInfo.AngleLimit(rotation.Z));
                    point.Scale = MapInfo.ReadVector(command, 6);
                    break;
                case "FUNC_DATA_END":
                    point = null;
                    break;
            }
        }
        return layout;
    }

    private static MapFuncPoint? CreateFuncPoint(ScriptCommand command)
    {
        var type = command.Arg(0).AsString switch
        {
            "effect" => MapFuncPointType.Effect,
            "fire" => MapFuncPointType.Fire,
            "flare" => MapFuncPointType.Flare,
            "plight" => MapFuncPointType.PointLight,
            "anime" => MapFuncPointType.Anime,
            "invent" => MapFuncPointType.Invent,
            "event" => MapFuncPointType.Event,
            "sound" => MapFuncPointType.Sound,
            "pos" => MapFuncPointType.Position,
            _ => MapFuncPointType.None,
        };
        return type == MapFuncPointType.None ? null : new MapFuncPoint { Type = type, Enable = command.Arg(1).AsInt != 0 };
    }
}
