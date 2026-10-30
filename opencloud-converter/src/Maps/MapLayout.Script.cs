using System.Collections.Generic;
using System.Numerics;
using OpenCloud.Core.Scripts;

namespace OpenCloud.Assets.Maps;

/// <summary>Reads the layout tags of a retail <c>.map</c> script.</summary>
public sealed partial class MapLayout
{
    /// <summary>
    /// <c>map_tag</c> in retail order; binary scripts index it. Its first 35 entries are
    /// placeholders that keep the map information tags out of this table.
    /// </summary>
    public static readonly IReadOnlyList<string> Tags = BuildTags();

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
    }}
