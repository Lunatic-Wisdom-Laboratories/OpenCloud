using System;
using System.Collections.Generic;
using System.Numerics;

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
public sealed partial class MapLayout
{
    public List<MapPartsDefinition> Parts { get; } = new();
    public List<MapPlacement> Placements { get; } = new();
    public List<MapFuncPoint> FuncPoints { get; } = new();

    /// <summary>The first definition of the name, as <c>CMap::GetParts</c> walks its list.</summary>
    public MapPartsDefinition? FindParts(string name) =>
        Parts.Find(p => string.Equals(p.Name, name, StringComparison.Ordinal));


}
