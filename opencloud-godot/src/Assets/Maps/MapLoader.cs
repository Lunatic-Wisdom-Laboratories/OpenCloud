using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Models;
using OpenCloud.Core;
using OpenCloud.Core.IO;

namespace OpenCloud.Assets.Maps;

/// <summary>
/// Builds a converted map (<see cref="MapDocument"/>) as Godot nodes, the way <c>CScene::LoadMap</c>
/// lays it out: each placement of a part definition is a <see cref="Node3D"/> with the
/// placement's transform; its pieces hold copies of their models from the map's model lists,
/// positioned as <c>mgCFrame::GetLocalMatrix</c> positions a frame. Collision models become
/// <see cref="StaticBody3D"/>s with a hidden debug mesh; the sky is a <see cref="MapSky"/>.
/// </summary>
public sealed class MapLoader
{
    /// <summary>Physics layer of walkable collision (MDS type 1).</summary>
    public const uint CollisionLayer = 1;

    /// <summary>Physics layer of camera collision (MDS type 3).</summary>
    public const uint CameraCollisionLayer = 2;

    /// <summary>Render layer of pieces drawn without the map's directional lights (LIGHT_FLAG).</summary>
    public const uint NoLightRenderLayer = 2;

    public const string CollisionGroup = "map_collision";
    public const string FuncPointGroup = "map_func_points";

    private readonly VirtualFileSystem _vfs;
    private readonly ModelLoader _models;

    public MapLoader(VirtualFileSystem vfs, ModelLoader models)
    {
        _vfs = vfs;
        _models = models;
    }

    /// <summary>Every converted map of the mounted data.</summary>
    public static IEnumerable<string> EnumerateMaps(VirtualFileSystem vfs) =>
        vfs.EnumerateFiles()
            .Where(path => path.StartsWith("map/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(MapDocument.Extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>GetMapPath</c> (mapselect): <c>s01</c> → <c>map/s/s01/s01.map.json</c>, <c>d01f01</c> →
    /// <c>map/d/d01/f01/d01f01.map.json</c>. A path that already names a map document is kept.
    /// </summary>
    public static string MapPath(string name)
    {
        name = VfsPath.Normalize(name);
        if (name.EndsWith(MapDocument.Extension, StringComparison.OrdinalIgnoreCase))
            return name;
        if (VfsPath.GetExtension(name) == "map")
            return MapDocument.PathFor(name);
        var path = "map/" + name[..Math.Min(1, name.Length)] + "/";
        if (name.Length >= 3)
            path += name[..3] + "/";
        if (name.Length >= 6)
            path += name[3..6] + "/";
        return path + name + MapDocument.Extension;
    }

    /// <summary>Statistics of the last load, for the viewer and the smoke test.</summary>
    public sealed class Report
    {
        public int ModelLists;
        public int Models;
        public int Placements;
        public int PlacedPieces;
        public int MeshInstances;
        public int CollisionTriangles;
        public int Characters;
        public int FuncPoints;
        public readonly SortedSet<string> MissingModels = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedSet<string> MissingParts = new(StringComparer.Ordinal);
        public readonly List<string> Notes = new();
    }

    /// <summary>A list entry's model, loaded once and copied for every piece that uses it.</summary>
    private sealed class ModelTemplate
    {
        public required MapModelEntry Entry { get; init; }
        public required Node3D Scene { get; init; }
    }

    private readonly Dictionary<string, ModelTemplate?> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Directory, MapModelListDocument List)> _lists = new();
    private Report _report = new();

    public LoadedMap Load(string path, float clock = 12.0f)
    {
        path = MapPath(path);
        var json = _vfs.TryRead(path) ?? throw new System.IO.FileNotFoundException($"Map '{path}' not found in the converted data.");
        var document = MapDocument.FromJson(JsonValues.Parse(json, path), path);
        string stem = path[..^MapDocument.Extension.Length];

        _report = new Report();
        FreeTemplates();
        _lists.Clear();
        LoadModelLists(document);

        var root = new Node3D { Name = NodeNames.Safe(VfsPath.GetFileName(stem)) };
        var map = new LoadedMap { Path = path, Root = root, Info = document.Info, Layout = document.Layout, Report = _report, Clock = clock };

        var parts = new Node3D { Name = "Parts" };
        root.AddChild(parts);
        foreach (var placement in document.Layout.Placements)
            PlaceParts(map, parts, placement);

        if (document.Layout.FuncPoints.Count > 0)
        {
            var points = new Node3D { Name = "FuncPoints" };
            root.AddChild(points);
            foreach (var point in document.Layout.FuncPoints)
                AddFuncPoint(points, point);
        }

        if (document.Sky != null)
        {
            try
            {
                map.Sky = MapSky.Load(_vfs, _models, document.Sky);
                root.AddChild(map.Sky);
            }
            catch (Exception exception) when (exception is System.IO.IOException or System.IO.InvalidDataException)
            {
                _report.Notes.Add($"sky: {exception.Message}");
            }
        }

        FreeTemplates();
        map.SetClock(clock);
        return map;
    }

    private void FreeTemplates()
    {
        foreach (var template in _templates.Values)
            template?.Scene.Free();
        _templates.Clear();
    }

    private void LoadModelLists(MapDocument document)
    {
        foreach (var path in document.ModelLists)
        {
            var json = _vfs.TryRead(path);
            if (json == null)
            {
                _report.Notes.Add($"model list {path} not found");
                continue;
            }
            var list = MapModelListDocument.FromJson(JsonValues.Parse(json, path), path);
            _lists.Add((VfsPath.GetDirectory(path), list));
            _report.Models += list.Entries.Count;
        }
        _report.ModelLists = _lists.Count;
    }

    /// <summary>
    /// <c>CMap::SearchMDS</c>. <c>CMdsListSet::LoadPCPFile</c> fills the last free slot, so the
    /// lists loaded later sit in lower slots and are searched first.
    /// </summary>
    private ModelTemplate? FindModel(string name)
    {
        if (_templates.TryGetValue(name, out var cached))
            return cached;

        ModelTemplate? template = null;
        for (int i = _lists.Count - 1; i >= 0 && template == null; i--)
        {
            var (directory, list) = _lists[i];
            var entry = list.Find(name);
            if (entry == null)
                continue;
            try
            {
                template = new ModelTemplate { Entry = entry, Scene = _models.Load(VfsPath.Combine(directory, entry.File)) };
            }
            catch (Exception exception) when (exception is System.IO.IOException or System.IO.InvalidDataException)
            {
                _report.Notes.Add($"{list.Name}/{entry.Name}: {exception.Message}");
            }
        }
        _templates[name] = template;
        if (template == null)
            _report.MissingModels.Add(name);
        return template;
    }

    /// <summary>
    /// <c>CMap::PlaceParts</c> + <c>mapMAP_PARTS_END</c>: copies the named part definition (only
    /// its pieces without a collision type, as <c>CMapParts::Copy</c> does) to the placement.
    /// </summary>
    private void PlaceParts(LoadedMap map, Node3D parent, MapPlacement placement)
    {
        var definition = map.Layout.FindParts(placement.Definition);
        if (definition == null)
        {
            _report.MissingParts.Add(placement.Definition);
            return;
        }
        _report.Placements++;

        var node = new Node3D
        {
            Name = NodeNames.Safe(placement.Name),
            Transform = MapTransforms.Frame(placement.Position, placement.Rotation, placement.Scale, Transform3D.Identity),
        };
        node.SetMeta("definition", definition.Name);
        if (placement.Group.Length > 0)
            node.SetMeta("group", placement.Group);
        parent.AddChild(node);

        StaticBody3D? collision = null;
        foreach (var piece in definition.Pieces)
        {
            if (piece.CollisionType != 0)
                continue;
            var template = FindModel(piece.Model);
            if (template == null)
                continue;
            _report.PlacedPieces++;

            if (template.Entry.Type == MapModelType.Character)
            {
                var character = (Node3D)template.Scene.Duplicate();
                character.Name = NodeNames.Safe(piece.Name);
                character.Transform = MapTransforms.Frame(piece.Position, piece.Rotation, piece.Scale, Transform3D.Identity);
                node.AddChild(character);
                map.Pieces.Add(new LoadedMap.PlacedPiece(character, placement, piece));
                _report.Characters++;
                continue;
            }

            var pieceNode = PlacePiece(template, piece);
            if (!template.Entry.IsDrawn)
            {
                collision ??= CreateCollisionBody(node);
                AddCollision(collision, piece, template, pieceNode);
                pieceNode.Free();
                continue;
            }

            foreach (var mesh in ModelLoader.FindAll<MeshInstance3D>(pieceNode))
            {
                if (definition.NoLight)
                    mesh.Layers = NoLightRenderLayer;
                _report.MeshInstances++;
            }
            node.AddChild(pieceNode);
            map.Pieces.Add(new LoadedMap.PlacedPiece(pieceNode, placement, piece));
        }

        foreach (var point in definition.FuncPoints)
            AddFuncPoint(node, point);
    }

    /// <summary>
    /// A copy of a model's frames for one piece: each root frame keeps its own matrix inside the
    /// piece transform (<c>MapTransforms.Frame</c>), children follow their parents.
    /// </summary>
    private static Node3D PlacePiece(ModelTemplate template, MapPieceDefinition piece)
    {
        var pieceNode = new Node3D { Name = NodeNames.Safe(piece.Name) };
        foreach (var frame in template.Scene.GetChildren().OfType<Node3D>())
        {
            var copy = (Node3D)frame.Duplicate();
            copy.Transform = MapTransforms.Frame(piece.Position, piece.Rotation, piece.Scale, frame.Transform);
            pieceNode.AddChild(copy);
        }
        return pieceNode;
    }

    private static StaticBody3D CreateCollisionBody(Node3D parent)
    {
        var body = new StaticBody3D { Name = "Collision", CollisionLayer = 0, CollisionMask = 0 };
        parent.AddChild(body);
        return body;
    }

    /// <summary>
    /// A collision piece (<c>LoadCollisionFile</c>): its triangles become a concave shape, on the
    /// walk or camera layer, plus a debug mesh shown with the viewer's collision toggle.
    /// </summary>
    private void AddCollision(StaticBody3D body, MapPieceDefinition piece, ModelTemplate template, Node3D pieceNode)
    {
        var corners = new List<Vector3>();
        CollectTriangles(pieceNode, Transform3D.Identity, corners);
        if (corners.Count == 0)
            return;
        bool camera = template.Entry.Type == MapModelType.CameraCollision;
        body.CollisionLayer |= camera ? CameraCollisionLayer : CollisionLayer;

        var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
        shape.SetFaces(corners.ToArray());
        body.AddChild(new CollisionShape3D { Name = NodeNames.Safe(piece.Name), Shape = shape });
        _report.CollisionTriangles += corners.Count / 3;

        var debug = DebugMesh(corners, camera ? new Color(1.0f, 0.6f, 0.1f, 0.35f) : new Color(0.1f, 0.9f, 0.4f, 0.35f));
        debug.Name = NodeNames.Safe(piece.Name) + "_debug";
        debug.Visible = false;
        debug.AddToGroup(CollisionGroup);
        body.AddChild(debug);
    }

    /// <summary>Every triangle of the meshes under a node, in the node's parent space.</summary>
    private static void CollectTriangles(Node node, Transform3D parent, List<Vector3> corners)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is not Node3D spatial)
                continue;
            var transform = parent * spatial.Transform;
            if (spatial is MeshInstance3D { Mesh: { } mesh })
            {
                foreach (var corner in mesh.GetFaces())
                    corners.Add(transform * corner);
            }
            CollectTriangles(spatial, transform, corners);
        }
    }

    private static MeshInstance3D DebugMesh(List<Vector3> corners, Color colour)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = corners.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            AlbedoColor = colour,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = false,
        });
        return new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }

    private static readonly SphereMesh MarkerMesh = new() { Radius = 1.0f, Height = 2.0f, RadialSegments = 12, Rings = 6 };

    /// <summary>
    /// A function point as a translucent ellipsoid: its scale is the trigger radius used by
    /// <c>CheckFuncEvent</c>. Hidden until the viewer shows points.
    /// </summary>
    private void AddFuncPoint(Node3D parent, MapFuncPoint point)
    {
        _report.FuncPoints++;
        var colour = point.Type switch
        {
            MapFuncPointType.Event => new Color(1.0f, 0.85f, 0.1f, 0.3f),
            MapFuncPointType.PointLight or MapFuncPointType.Fire or MapFuncPointType.Flare => new Color(1.0f, 0.4f, 0.1f, 0.3f),
            MapFuncPointType.Sound => new Color(0.3f, 0.6f, 1.0f, 0.3f),
            _ => new Color(0.8f, 0.3f, 1.0f, 0.3f),
        };
        var marker = new MeshInstance3D
        {
            Name = NodeNames.Safe($"{point.Type}_{point.Name}"),
            Mesh = MarkerMesh,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = colour,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
            Transform = MapTransforms.Frame(point.Position, point.Rotation, point.Scale, Transform3D.Identity),
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        marker.AddToGroup(FuncPointGroup);
        marker.AddChild(new Label3D
        {
            Text = $"{point.Type} {point.Name}",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            PixelSize = 0.05f,
            NoDepthTest = true,
            FixedSize = true,
            FontSize = 12,
        });
        parent.AddChild(marker);
    }

}

/// <summary>A built map: its nodes, the data they came from, and the time-of-day state.</summary>
public sealed class LoadedMap
{
    public sealed record PlacedPiece(Node3D Node, MapPlacement Placement, MapPieceDefinition Piece);

    public required string Path { get; init; }
    public required Node3D Root { get; init; }
    public required MapInfo Info { get; init; }
    public required MapLayout Layout { get; init; }
    public required MapLoader.Report Report { get; init; }
    public MapSky? Sky { get; set; }
    public List<PlacedPiece> Pieces { get; } = new();

    /// <summary>The game clock in hours; the map uses it only when its TIME_FLAG enables it.</summary>
    public float Clock { get; set; }

    /// <summary>Shows pieces whose script hides them (PIECE/MAP_PARTS show flag 0).</summary>
    public bool ShowHidden { get; private set; }

    /// <summary>The map's time (<c>CMap::GetNowTime</c>) at the current clock.</summary>
    public float NowTime => Info.NowTime(Clock);

    public MapLightingSet? Lighting => Info.SelectLighting(Clock);

    public void SetClock(float clock)
    {
        Clock = (clock % 24.0f + 24.0f) % 24.0f;
        UpdateVisibility();
        Sky?.SetTimeBand(MapInfo.TimeBand(NowTime));
    }

    public void SetShowHidden(bool show)
    {
        ShowHidden = show;
        UpdateVisibility();
    }

    /// <summary>Piece visibility: its show flags and its PIECE_TIME interval at the map's time.</summary>
    private void UpdateVisibility()
    {
        float now = NowTime;
        foreach (var placed in Pieces)
            placed.Node.Visible = (ShowHidden || (placed.Placement.Show && placed.Piece.Show)) && placed.Piece.IsActiveAt(now);
    }

    /// <summary>Bounds of the visible drawn pieces in map space.</summary>
    public Aabb ComputeBounds()
    {
        Aabb? bounds = null;
        void Visit(Node node, Transform3D parent)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is not Node3D { Visible: true } spatial)
                    continue;
                var transform = parent * spatial.Transform;
                if (spatial is MeshInstance3D { Mesh: { } mesh })
                {
                    var box = transform * mesh.GetAabb();
                    bounds = bounds?.Merge(box) ?? box;
                }
                Visit(spatial, transform);
            }
        }
        if (Root.GetNodeOrNull("Parts") is Node3D parts)
            Visit(parts, parts.Transform);
        return bounds ?? new Aabb(new Vector3(-50, -50, -50), new Vector3(100, 100, 100));
    }
}
