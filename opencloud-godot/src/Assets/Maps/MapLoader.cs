using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Models;
using OpenCloud.Assets.Textures;
using OpenCloud.Core.IO;
using OpenCloud.Core.Scripts;
using OpenCloud.Rendering;

namespace OpenCloud.Assets.Maps;

/// <summary>
/// Loads a map the way <c>CScene::LoadMap</c> does and builds it as
/// Godot nodes. A map <c>dir/name</c> is a set of files sharing one stem:
/// <list type="bullet">
/// <item><c>.map</c>: script read twice, for the map information (<see cref="MapInfo"/>) and the
/// layout (<see cref="MapLayout"/>);</item>
/// <item><c>.ipk</c>: PACK of the IMG texture files the information names;</item>
/// <item><c>.mpk</c>: PACK of the PCP model lists (<see cref="MapModelList"/>);</item>
/// <item><c>.sky</c>: sky pack (<c>map/def.sky</c> when the map has none), see <see cref="MapSky"/>;</item>
/// <item><c>.cfg</c>, <c>.efp</c>: water, draw-off areas, extra event points and effects (not read yet).</item>
/// </list>
/// Each placed part is a <see cref="Node3D"/> with the placement's transform; its pieces hold
/// the meshes of their models, positioned as <c>mgCFrame::GetLocalMatrix</c> positions a frame.
/// Collision models become <see cref="StaticBody3D"/>s with a hidden debug mesh.
/// </summary>
public sealed class MapLoader
{
    /// <summary>Retail <c>mds_list_num</c>: the scene holds at most eight model lists.</summary>
    public const int MaxModelLists = 8;

    /// <summary>Physics layer of walkable collision (MDS type 1).</summary>
    public const uint CollisionLayer = 1;

    /// <summary>Physics layer of camera collision (MDS type 3).</summary>
    public const uint CameraCollisionLayer = 2;

    /// <summary>Render layer of pieces drawn without the map's directional lights (LIGHT_FLAG).</summary>
    public const uint NoLightRenderLayer = 2;

    public const string CollisionGroup = "map_collision";
    public const string FuncPointGroup = "map_func_points";

    private readonly VirtualFileSystem _vfs;
    private readonly MdsModelLoader _models;

    public MapLoader(VirtualFileSystem vfs, MdsModelLoader models)
    {
        _vfs = vfs;
        _models = models;
    }

    /// <summary>Every map script of the mounted data.</summary>
    public static IEnumerable<string> EnumerateMaps(VirtualFileSystem vfs) =>
        vfs.EnumerateFiles("map")
            .Where(path => path.StartsWith("map/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>GetMapPath</c> (mapselect): <c>s01</c> → <c>map/s/s01/s01.map</c>, <c>d01f01</c> →
    /// <c>map/d/d01/f01/d01f01.map</c>. A path that already ends in <c>.map</c> is kept.
    /// </summary>
    public static string MapPath(string name)
    {
        name = VfsPath.Normalize(name);
        if (VfsPath.GetExtension(name) == "map")
            return name;
        var path = "map/" + name[..Math.Min(1, name.Length)] + "/";
        if (name.Length >= 3)
            path += name[..3] + "/";
        if (name.Length >= 6)
            path += name[3..6] + "/";
        return path + name + ".map";
    }

    /// <summary>Statistics of the last load, for the viewer and the smoke test.</summary>
    public sealed class Report
    {
        public int ModelLists;
        public int Models;
        public int Textures;
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

    /// <summary>A model of the lists, built once and shared by every piece that uses it.</summary>
    private sealed class ModelTemplate
    {
        public required MapModelEntry Entry { get; init; }
        public MdsFile? Mds { get; init; }
        public Dictionary<int, ArrayMesh> Meshes { get; init; } = new();
        public PackArchive? CharacterPack { get; init; }
    }

    private readonly Dictionary<string, ModelTemplate?> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MapModelList> _lists = new();
    private TextureLibrary _textures = new();
    private Report _report = new();

    public LoadedMap Load(string path, float clock = 12.0f)
    {
        path = MapPath(path);
        var script = _vfs.TryRead(path) ?? throw new System.IO.FileNotFoundException($"Map script '{path}' not found.");
        string stem = path[..^".map".Length];

        _report = new Report();
        _templates.Clear();
        _lists.Clear();
        _textures = new TextureLibrary();
        _models.ResetReport();

        var info = MapInfo.Parse(GameScript.Parse(script, MapInfo.Tags));
        var layout = MapLayout.Parse(GameScript.Parse(script, MapLayout.Tags));
        LoadTextures(stem, info);
        LoadModelLists(stem, info);

        var root = new Node3D { Name = MdsModelLoader.SafeNodeName(VfsPath.GetFileName(stem)) };
        var map = new LoadedMap { Path = path, Root = root, Info = info, Layout = layout, Report = _report, Clock = clock };

        var parts = new Node3D { Name = "Parts" };
        root.AddChild(parts);
        foreach (var placement in layout.Placements)
            PlaceParts(map, parts, placement);

        if (layout.FuncPoints.Count > 0)
        {
            var points = new Node3D { Name = "FuncPoints" };
            root.AddChild(points);
            foreach (var point in layout.FuncPoints)
                AddFuncPoint(points, point);
        }

        map.Sky = LoadSky(stem, info);
        if (map.Sky != null)
            root.AddChild(map.Sky);

        if (_vfs.Exists(stem + ".cfg"))
            _report.Notes.Add("map .cfg (water, draw-off areas, extra event points) is not read yet");
        map.SetClock(clock);
        return map;
    }

    /// <summary><c>CMap::LoadData</c>: enters each IMG the map names from its <c>.ipk</c>.</summary>
    private void LoadTextures(string stem, MapInfo info)
    {
        var ipk = _vfs.TryOpenPack(stem + ".ipk");
        if (ipk == null)
        {
            if (info.TexturePacks.Count > 0)
                _report.Notes.Add($"{stem}.ipk not found: {info.TexturePacks.Count} texture files missing");
            return;
        }
        foreach (var name in info.TexturePacks)
        {
            var data = ipk.TryRead(name);
            if (data == null)
            {
                _report.Notes.Add($"texture file {name} not in {stem}.ipk");
                continue;
            }
            MdsModelLoader.TryAddTextures(_textures, name, data);
        }
        _report.Textures = _textures.Count;
    }

    /// <summary><c>CMap::LoadData</c>: loads each PCP the map names from its <c>.mpk</c>.</summary>
    private void LoadModelLists(string stem, MapInfo info)
    {
        var mpk = _vfs.TryOpenPack(stem + ".mpk");
        if (mpk == null)
        {
            if (info.ModelPacks.Count > 0)
                _report.Notes.Add($"{stem}.mpk not found: {info.ModelPacks.Count} model lists missing");
            return;
        }
        foreach (var name in info.ModelPacks)
        {
            if (_lists.Count >= MaxModelLists || _lists.Any(l => string.Equals(l.Name, name, StringComparison.Ordinal)))
                continue;
            var data = mpk.TryRead(name);
            var pack = data == null ? null : PackArchive.TryParse(data, name);
            if (pack == null)
            {
                _report.Notes.Add($"model list {name} not in {stem}.mpk");
                continue;
            }
            var list = MapModelList.Parse(name, pack);
            _lists.Add(list);
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
            var entry = _lists[i].Find(name);
            if (entry != null)
                template = BuildTemplate(_lists[i], entry);
        }
        _templates[name] = template;
        if (template == null)
            _report.MissingModels.Add(name);
        return template;
    }

    private ModelTemplate? BuildTemplate(MapModelList list, MapModelEntry entry)
    {
        var data = list.Pack.TryRead(entry.Name);
        if (data == null)
            return null;

        if (entry.Type == MapModelType.Character)
        {
            var pack = PackArchive.TryParse(data, entry.Name);
            return pack == null ? null : new ModelTemplate { Entry = entry, CharacterPack = pack };
        }

        if (!MdsFile.LooksLikeMds(data))
        {
            _report.Notes.Add($"{list.Name}/{entry.Name} is not an MDS scene");
            return null;
        }
        try
        {
            var mds = MdsFile.Parse(data);
            var meshes = entry.IsDrawn ? _models.BuildObjectMeshes(mds, _textures) : new Dictionary<int, ArrayMesh>();
            return new ModelTemplate { Entry = entry, Mds = mds, Meshes = meshes };
        }
        catch (System.IO.InvalidDataException exception)
        {
            _report.Notes.Add($"{list.Name}/{entry.Name}: {exception.Message}");
            return null;
        }
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
            Name = MdsModelLoader.SafeNodeName(placement.Name),
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

            if (template.CharacterPack != null)
            {
                AddCharacter(map, node, placement, piece, template);
                continue;
            }

            var mds = template.Mds!;
            var transforms = MdsModelLoader.ObjectTransforms(mds, local => MapTransforms.Frame(piece.Position, piece.Rotation, piece.Scale, local));
            if (!template.Entry.IsDrawn)
            {
                collision ??= CreateCollisionBody(node);
                AddCollision(collision, piece, template, transforms);
                continue;
            }

            var pieceNode = new Node3D { Name = MdsModelLoader.SafeNodeName(piece.Name) };
            node.AddChild(pieceNode);
            foreach (var (index, mesh) in template.Meshes)
            {
                var instance = new MeshInstance3D { Name = MdsModelLoader.SafeNodeName(mds.Objects[index].BaseName), Mesh = mesh, Transform = transforms[index] };
                if (definition.NoLight)
                    instance.Layers = NoLightRenderLayer;
                pieceNode.AddChild(instance);
                _report.MeshInstances++;
            }
            map.Pieces.Add(new LoadedMap.PlacedPiece(pieceNode, placement, piece));
        }

        foreach (var point in definition.FuncPoints)
            AddFuncPoint(node, point);
    }

    private void AddCharacter(LoadedMap map, Node3D parent, MapPlacement placement, MapPieceDefinition piece, ModelTemplate template)
    {
        try
        {
            var character = _models.LoadPack(template.CharacterPack!, template.Entry.Name, _textures);
            character.Name = MdsModelLoader.SafeNodeName(piece.Name);
            character.Transform = MapTransforms.Frame(piece.Position, piece.Rotation, piece.Scale, Transform3D.Identity);
            parent.AddChild(character);
            map.Pieces.Add(new LoadedMap.PlacedPiece(character, placement, piece));
            _report.Characters++;
        }
        catch (System.IO.InvalidDataException exception)
        {
            _report.Notes.Add($"character {template.Entry.Name}: {exception.Message}");
        }
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
    private void AddCollision(StaticBody3D body, MapPieceDefinition piece, ModelTemplate template, Transform3D[] transforms)
    {
        var corners = MdsModelLoader.CollectTriangles(template.Mds!, transforms);
        if (corners.Count == 0)
            return;
        bool camera = template.Entry.Type == MapModelType.CameraCollision;
        body.CollisionLayer |= camera ? CameraCollisionLayer : CollisionLayer;

        var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
        shape.SetFaces(corners.ToArray());
        body.AddChild(new CollisionShape3D { Name = MdsModelLoader.SafeNodeName(piece.Name), Shape = shape });
        _report.CollisionTriangles += corners.Count / 3;

        var debug = DebugMesh(corners, camera ? new Color(1.0f, 0.6f, 0.1f, 0.35f) : new Color(0.1f, 0.9f, 0.4f, 0.35f));
        debug.Name = MdsModelLoader.SafeNodeName(piece.Name) + "_debug";
        debug.Visible = false;
        debug.AddToGroup(CollisionGroup);
        body.AddChild(debug);
    }

    private static MeshInstance3D DebugMesh(List<Vector3> corners, Color colour)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        // Reverse each triangle to Godot's clockwise front faces.
        var vertices = new Vector3[corners.Count];
        for (int i = 0; i + 2 < corners.Count; i += 3)
        {
            vertices[i] = corners[i];
            vertices[i + 1] = corners[i + 2];
            vertices[i + 2] = corners[i + 1];
        }
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
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
            Name = MdsModelLoader.SafeNodeName($"{point.Type}_{point.Name}"),
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

    /// <summary>
    /// The map's sky pack: <c>&lt;stem&gt;.sky</c>, else <c>map/def.sky</c>, loaded when SKY_INFO
    /// enables it (<c>CScene::LoadMapFromMemory</c>).
    /// </summary>
    private MapSky? LoadSky(string stem, MapInfo info)
    {
        if (info.SkyInfo == 0)
            return null;
        var pack = _vfs.TryOpenPack(stem + ".sky") ?? _vfs.TryOpenPack("map/def.sky");
        if (pack == null)
        {
            _report.Notes.Add("sky pack not found");
            return null;
        }
        try
        {
            return MapSky.Build(pack, _models);
        }
        catch (System.IO.InvalidDataException exception)
        {
            _report.Notes.Add($"sky: {exception.Message}");
            return null;
        }
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
