using System.Collections.Generic;
using Godot;
using OpenCloud.Assets.Models;
using OpenCloud.Core;
using OpenCloud.Core.IO;

namespace OpenCloud.Assets.Maps;

/// <summary>
/// A map's sky (<c>CMapSky</c>): per time band a back sky and a sky
/// model, each drawn at the camera, spinning about Y, and drawn a second time mirrored below the
/// horizon (scale 1,-1,1), plus named frames that spin on their own. Each band's models use the
/// textures of that band's IMG. Only the current band is shown; the sun/moon models and the
/// SKY_BG background are not drawn yet.
/// </summary>
public partial class MapSky : Node3D
{
    /// <summary>Spin speeds are per game frame; this engine counts 30 game frames a second.</summary>
    public const float GameFramesPerSecond = 30.0f;

    private sealed class Band
    {
        public Node3D Root = null!;
        public readonly List<(Node3D Node, float Speed)> Spinning = new();
        public readonly List<(Node3D Node, float Speed)> Frames = new();
    }

    private readonly Band?[] _bands = new Band?[MapSkyInfo.Bands];
    private MapTimeBand _band = MapTimeBand.Day;

    public MapSkyInfo Info { get; private set; } = null!;

    /// <summary>Loads a converted sky pack (<see cref="MapSkyDocument"/>).</summary>
    public static MapSky Load(VirtualFileSystem vfs, ModelLoader models, string documentPath)
    {
        var json = vfs.TryRead(documentPath) ?? throw new System.IO.FileNotFoundException($"Sky '{documentPath}' not found.");
        var document = MapSkyDocument.FromJson(JsonValues.Parse(json, documentPath), documentPath);
        string directory = VfsPath.GetDirectory(documentPath);
        var sky = new MapSky { Name = "Sky", Info = document.Info };

        for (int band = 0; band < MapSkyInfo.Bands; band++)
        {
            var entry = new Band { Root = new Node3D { Name = $"Band{band}", Visible = false } };
            // The back sky is drawn first, then the sky.
            AddSkyModel(entry, vfs, models, directory, document.BackSkyFiles[band], sky.Info.BackSkySpeeds[band], "Back");
            AddSkyModel(entry, vfs, models, directory, document.SkyFiles[band], sky.Info.SkySpeeds[band], "Front");
            if (entry.Root.GetChildCount() == 0)
            {
                entry.Root.Free();
                continue;
            }

            foreach (var animation in sky.Info.Animations)
            {
                if (animation.Band != band)
                    continue;
                var holder = entry.Root.GetNodeOrNull(animation.Back ? "Back" : "Front");
                foreach (var copy in holder?.GetChildren() ?? new Godot.Collections.Array<Node>())
                {
                    if (copy.FindChild(NodeNames.Safe(animation.Frame), true, false) is Node3D frame)
                        entry.Frames.Add((frame, animation.Speed));
                }
            }
            sky.AddChild(entry.Root);
            sky._bands[band] = entry;
        }
        return sky;
    }

    private static void AddSkyModel(Band band, VirtualFileSystem vfs, ModelLoader models, string directory, string? file, float speed, string role)
    {
        if (file == null)
            return;
        string path = VfsPath.Combine(directory, file);
        if (!vfs.Exists(path))
            return;
        var model = models.Load(path);
        foreach (var mesh in ModelLoader.FindAll<MeshInstance3D>(model))
            MakeSkyMaterials(mesh);

        var holder = new Node3D { Name = role };
        band.Root.AddChild(holder);
        foreach (bool mirrored in new[] { false, true })
        {
            var copy = mirrored ? (Node3D)model.Duplicate() : model;
            copy.Name = mirrored ? "Mirror" : "Model";
            copy.Scale = mirrored ? new Vector3(1, -1, 1) : Vector3.One;
            holder.AddChild(copy);
        }
        band.Spinning.Add((holder, speed));
    }

    /// <summary>The sky ignores the map's lights and fog.</summary>
    private static void MakeSkyMaterials(MeshInstance3D instance)
    {
        for (int surface = 0; surface < instance.Mesh?.GetSurfaceCount(); surface++)
        {
            if (instance.Mesh!.SurfaceGetMaterial(surface) is StandardMaterial3D material)
            {
                material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                material.DisableFog = true;
            }
        }
    }

    public void SetTimeBand(MapTimeBand band)
    {
        _band = band;
        for (int i = 0; i < _bands.Length; i++)
        {
            if (_bands[i] is { } entry)
                entry.Root.Visible = i == (int)band;
        }
    }

    public bool HasBand(MapTimeBand band) => _bands[(int)band] != null;

    public override void _Process(double delta)
    {
        if (GetViewport().GetCamera3D() is { } camera)
            GlobalPosition = camera.GlobalPosition;

        if (_bands[(int)_band] is not { } entry)
            return;
        float frames = (float)delta * GameFramesPerSecond;
        foreach (var (node, speed) in entry.Spinning)
            node.Rotation = new Vector3(0, Mathf.Wrap(node.Rotation.Y + speed * frames, -Mathf.Pi, Mathf.Pi), 0);
        foreach (var (node, speed) in entry.Frames)
            node.Rotation = node.Rotation with { Y = Mathf.Wrap(node.Rotation.Y + speed * frames, -Mathf.Pi, Mathf.Pi) };
    }
}
