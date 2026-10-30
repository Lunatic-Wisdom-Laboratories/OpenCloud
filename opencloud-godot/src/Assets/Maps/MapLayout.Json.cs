using System;
using System.Linq;
using System.Text.Json.Nodes;
using static OpenCloud.Core.JsonValues;

namespace OpenCloud.Assets.Maps;

public sealed partial class MapLayout
{
    public JsonObject ToJson() => new()
    {
        ["parts"] = new JsonArray(Parts.Select(p => (JsonNode?)new JsonObject
        {
            ["name"] = p.Name,
            ["far_distance"] = p.FarDistance,
            ["far_fade"] = p.FarFade,
            ["no_light"] = p.NoLight,
            ["no_point_light"] = p.NoPointLight,
            ["move_flags"] = p.MoveFlags,
            ["pieces"] = new JsonArray(p.Pieces.Select(PieceJson).ToArray()),
            ["func_points"] = new JsonArray(p.FuncPoints.Select(PointJson).ToArray()),
        }).ToArray()),
        ["placements"] = new JsonArray(Placements.Select(p => (JsonNode?)new JsonObject
        {
            ["name"] = p.Name,
            ["show"] = p.Show,
            ["definition"] = p.Definition,
            ["group"] = p.Group,
            ["position"] = Array(p.Position),
            ["rotation"] = Array(p.Rotation),
            ["scale"] = Array(p.Scale),
            ["far_distance"] = p.FarDistance,
            ["far_fade"] = p.FarFade,
        }).ToArray()),
        ["func_points"] = new JsonArray(FuncPoints.Select(PointJson).ToArray()),
    };

    private static JsonNode? PieceJson(MapPieceDefinition piece) => new JsonObject
    {
        ["model"] = piece.Model,
        ["name"] = piece.Name,
        ["show"] = piece.Show,
        ["position"] = Array(piece.Position),
        ["rotation"] = Array(piece.Rotation),
        ["scale"] = Array(piece.Scale),
        ["collision_type"] = piece.CollisionType,
        ["collision_param"] = piece.CollisionParam,
        ["time_start"] = piece.TimeStart,
        ["time_end"] = piece.TimeEnd,
        ["materials"] = new JsonArray(piece.Materials.Select(m => (JsonNode?)new JsonObject
        {
            ["frame"] = m.Frame,
            ["material_index"] = m.MaterialIndex,
            ["colour"] = Array(m.Colour),
            ["colour_index"] = m.ColourIndex,
        }).ToArray()),
    };

    private static JsonNode? PointJson(MapFuncPoint point)
    {
        var json = new JsonObject
        {
            ["type"] = point.Type.ToString(),
            ["enable"] = point.Enable,
            ["position"] = Array(point.Position),
            ["rotation"] = Array(point.Rotation),
            ["scale"] = Array(point.Scale),
        };
        if (point.Name != null)
            json["name"] = point.Name;
        return json;
    }

    public static MapLayout FromJson(JsonObject json)
    {
        var layout = new MapLayout();
        foreach (var p in Objects(json, "parts"))
        {
            var parts = new MapPartsDefinition
            {
                Name = Str(p, "name") ?? string.Empty,
                FarDistance = Float(p, "far_distance", -1.0f),
                FarFade = Int(p, "far_fade"),
                NoLight = Bool(p, "no_light"),
                NoPointLight = Bool(p, "no_point_light"),
                MoveFlags = Int(p, "move_flags"),
            };
            parts.Pieces.AddRange(Objects(p, "pieces").Select(ReadPiece));
            parts.FuncPoints.AddRange(Objects(p, "func_points").Select(ReadPoint));
            layout.Parts.Add(parts);
        }
        foreach (var p in Objects(json, "placements"))
        {
            layout.Placements.Add(new MapPlacement
            {
                Name = Str(p, "name") ?? string.Empty,
                Show = Bool(p, "show", true),
                Definition = Str(p, "definition") ?? string.Empty,
                Group = Str(p, "group") ?? string.Empty,
                Position = Vector3(p, "position"),
                Rotation = Vector3(p, "rotation"),
                Scale = Vector3(p, "scale", System.Numerics.Vector3.One),
                FarDistance = Float(p, "far_distance", -1.0f),
                FarFade = Int(p, "far_fade"),
            });
        }
        layout.FuncPoints.AddRange(Objects(json, "func_points").Select(ReadPoint));
        return layout;
    }

    private static MapPieceDefinition ReadPiece(JsonObject json)
    {
        var piece = new MapPieceDefinition
        {
            Model = Str(json, "model") ?? string.Empty,
            Name = Str(json, "name") ?? string.Empty,
            Show = Bool(json, "show", true),
            Position = Vector3(json, "position"),
            Rotation = Vector3(json, "rotation"),
            Scale = Vector3(json, "scale", System.Numerics.Vector3.One),
            CollisionType = Int(json, "collision_type"),
            CollisionParam = Int(json, "collision_param"),
            TimeStart = Float(json, "time_start"),
            TimeEnd = Float(json, "time_end"),
        };
        foreach (var m in Objects(json, "materials"))
            piece.Materials.Add(new MapPieceMaterial(Str(m, "frame") ?? string.Empty, Int(m, "material_index"), Vector4(m, "colour"), Int(m, "colour_index")));
        return piece;
    }

    private static MapFuncPoint ReadPoint(JsonObject json) => new()
    {
        Type = Enum.TryParse<MapFuncPointType>(Str(json, "type"), out var type) ? type : MapFuncPointType.None,
        Enable = Bool(json, "enable"),
        Name = Str(json, "name"),
        Position = Vector3(json, "position"),
        Rotation = Vector3(json, "rotation"),
        Scale = Vector3(json, "scale", System.Numerics.Vector3.One),
    };
}
