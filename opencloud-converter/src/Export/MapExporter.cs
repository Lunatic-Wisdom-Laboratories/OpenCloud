using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Maps;
using OpenCloud.Assets.Models;
using OpenCloud.Assets.Textures;
using OpenCloud.Core.IO;
using OpenCloud.Core.Scripts;

namespace OpenCloud.Converter.Export;

/// <summary>
/// Converts a map as <c>CScene::LoadMap</c> loads it, from the files that share its stem:
/// <list type="bullet">
/// <item><c>.map</c> → <c>.map.json</c> (<see cref="MapDocument"/>): the script read with the map
/// information tags and again with the layout tags;</item>
/// <item><c>.ipk</c> → <c>.ipk/&lt;img&gt;.textures.json</c>: the IMG files the information names;</item>
/// <item><c>.mpk</c> → <c>.mpk/&lt;list&gt;/list.json</c> (<see cref="MapModelListDocument"/>) with a GLB
/// per model, textured with the map's IMGs: drawn models as frame trees, collision models as
/// their triangles, character packs as characters;</item>
/// <item><c>.sky</c> (or <c>map/def.sky</c>) → <c>.sky/sky.json</c> (<see cref="MapSkyDocument"/>).</item>
/// </list>
/// The <c>.cfg</c> and <c>.efp</c> files are not converted yet.
/// </summary>
public sealed class MapExporter
{
    /// <summary>Retail <c>mds_list_num</c>: the scene holds at most eight model lists.</summary>
    public const int MaxModelLists = 8;

    private readonly RetailFileSystem _retail;
    private readonly MdsModelLoader _models;
    private readonly OutputTree _output;
    private readonly ConversionReport _report;
    private readonly Dictionary<string, string?> _skies = new(VfsPath.Comparer);

    public MapExporter(RetailFileSystem retail, MdsModelLoader models, OutputTree output, ConversionReport report)
    {
        _retail = retail;
        _models = models;
        _output = output;
        _report = report;
    }

    /// <summary>Original files a map conversion reads besides the script itself.</summary>
    public static IEnumerable<string> Companions(string mapPath)
    {
        string stem = mapPath[..^".map".Length];
        return new[] { ".ipk", ".mpk", ".sky" }.Select(extension => stem + extension);
    }

    public void Export(string path)
    {
        path = VfsPath.Normalize(path);
        var script = _retail.ReadAllBytes(path);
        string stem = path[..^".map".Length];
        string directory = VfsPath.GetDirectory(path);
        string name = VfsPath.GetFileName(stem);

        var info = MapInfo.Parse(GameScript.Parse(script, MapInfo.Tags));
        var layout = MapLayout.Parse(GameScript.Parse(script, MapLayout.Tags));
        var document = new MapDocument { Info = info, Layout = layout };

        var textures = ExportTextures(stem, info);
        foreach (var listPath in ExportModelLists(stem, info, textures))
            document.ModelLists.Add(listPath);

        if (info.SkyInfo != 0)
        {
            string skyPack = _retail.Exists(stem + ".sky") ? stem + ".sky" : "map/def.sky";
            document.Sky = ExportSky(skyPack);
            if (document.Sky == null)
                _report.Note(path, $"sky pack {skyPack} not found");
        }
        if (_retail.Exists(stem + ".cfg"))
            _report.Note(path, "map .cfg (water, draw-off areas, extra event points) is not converted yet");

        _output.WriteJson(VfsPath.Combine(directory, name + MapDocument.Extension), document.ToJson());
        _report.Maps++;
    }

    /// <summary><c>CMap::LoadData</c>: enters each IMG the map names from its <c>.ipk</c>.</summary>
    private TextureLibrary ExportTextures(string stem, MapInfo info)
    {
        var textures = new TextureLibrary();
        var ipk = _retail.TryOpenPack(stem + ".ipk");
        if (ipk == null)
        {
            if (info.TexturePacks.Count > 0)
                _report.Note(stem, $".ipk not found: {info.TexturePacks.Count} texture files missing");
            return textures;
        }
        foreach (var name in info.TexturePacks)
        {
            var data = ipk.TryRead(name);
            if (data == null)
            {
                _report.Note(stem, $"texture file {name} not in .ipk");
                continue;
            }
            MdsModelLoader.TryAddTextures(textures, name, data);
            TextureExporter.Export(_output, $"{stem}.ipk/{name}", data, stem + ".ipk");
        }
        return textures;
    }

    /// <summary><c>CMap::LoadData</c>: loads each PCP the map names from its <c>.mpk</c>.</summary>
    private IEnumerable<string> ExportModelLists(string stem, MapInfo info, TextureLibrary textures)
    {
        var mpk = _retail.TryOpenPack(stem + ".mpk");
        if (mpk == null)
        {
            if (info.ModelPacks.Count > 0)
                _report.Note(stem, $".mpk not found: {info.ModelPacks.Count} model lists missing");
            yield break;
        }

        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in info.ModelPacks)
        {
            if (done.Count >= MaxModelLists || !done.Add(name))
                continue;
            var data = mpk.TryRead(name);
            var pack = data == null ? null : PackArchive.TryParse(data, name);
            if (pack == null)
            {
                _report.Note(stem, $"model list {name} not in .mpk");
                continue;
            }
            yield return ExportModelList(stem, name, pack, textures);
        }
    }

    private string ExportModelList(string stem, string name, PackArchive pack, TextureLibrary textures)
    {
        string directory = $"{stem}.mpk/{OutputTree.SafeFileName(name)}";
        var list = MapModelList.Parse(name, pack);
        var document = new MapModelListDocument { Name = name };
        foreach (var entry in list.Entries)
        {
            string source = $"{stem}.mpk/{name}/{entry.Name}";
            var file = ExportListModel(source, directory, entry, pack, textures);
            if (file != null)
                document.Entries.Add(entry with { File = file });
        }
        string path = VfsPath.Combine(directory, MapModelListDocument.FileName);
        _output.WriteJson(path, document.ToJson());
        return path;
    }

    /// <summary>Converts one list entry; returns its GLB name relative to the list folder.</summary>
    private string? ExportListModel(string source, string directory, MapModelEntry entry, PackArchive pack, TextureLibrary textures)
    {
        var data = pack.TryRead(entry.Name);
        if (data == null)
            return null;
        string stem = System.IO.Path.GetFileNameWithoutExtension(entry.Name);
        string extension = VfsPath.GetExtension(entry.Name);
        try
        {
            if (entry.Type == MapModelType.Character)
            {
                var character = PackArchive.TryParse(data, entry.Name);
                if (character == null)
                {
                    _report.Note(source, "character entry is not a PACK");
                    return null;
                }
                var scene = _models.LoadPack(character, entry.Name, textures);
                return Write(source, directory, stem, extension, scene, ModelDocument.KindCharacter);
            }

            if (!MdsFile.LooksLikeMds(data))
            {
                _report.Note(source, "not an MDS scene");
                return null;
            }
            var mds = MdsFile.Parse(data);
            var meshes = entry.IsDrawn ? _models.BuildObjectMeshes(mds, textures) : MdsModelLoader.BuildCollisionMeshes(mds);
            var tree = MdsModelLoader.BuildFrameTree(mds, meshes, entry.Name);
            return Write(source, directory, stem, extension, tree, ModelDocument.KindModel);
        }
        catch (Exception exception) when (exception is System.IO.InvalidDataException or ArgumentException)
        {
            _report.Fail(source, exception.Message);
            return null;
        }
    }

    private string Write(string source, string directory, string stem, string extension, Node3D scene, string kind)
    {
        try
        {
            var model = ModelExporter.Export(scene, kind, source);
            string path = _output.Unique(directory, stem, extension, ".glb");
            _output.Write(path, model.Glb);
            if (model.Document.Models.Count > 0)
                _output.WriteJson(ModelDocument.PathFor(path), model.Document.ToJson());
            foreach (var warning in model.Warnings)
                _report.Note(source, warning);
            _report.Models++;
            return VfsPath.GetFileName(path);
        }
        finally
        {
            scene.Free();
        }
    }

    /// <summary>
    /// A sky pack (<c>CMapSky</c>): per time band, the sky and back-sky models textured with that
    /// band's IMG, drawn unlit. Converted once per pack; returns its document path.
    /// </summary>
    public string? ExportSky(string packPath)
    {
        packPath = VfsPath.Normalize(packPath);
        if (_skies.TryGetValue(packPath, out var done))
            return done;

        var pack = _retail.TryOpenPack(packPath);
        string? result = null;
        if (pack?.TryRead("info.cfg") is { } script)
        {
            var document = new MapSkyDocument { Info = MapSkyInfo.Parse(script) };
            for (int band = 0; band < MapSkyInfo.Bands; band++)
            {
                var textures = new TextureLibrary();
                if (document.Info.Images[band] is { } image && pack.TryRead(image) is { } imageData)
                    MdsModelLoader.TryAddTextures(textures, image, imageData);
                document.SkyFiles[band] = ExportSkyModel(packPath, pack, textures, document.Info.Skies[band], $"band{band}_sky");
                document.BackSkyFiles[band] = ExportSkyModel(packPath, pack, textures, document.Info.BackSkies[band], $"band{band}_back");
            }
            result = VfsPath.Combine(packPath, MapSkyDocument.FileName);
            _output.WriteJson(result, document.ToJson());
            _report.Skies++;
        }
        _skies[packPath] = result;
        return result;
    }

    private string? ExportSkyModel(string packPath, PackArchive pack, TextureLibrary textures, string? name, string fileStem)
    {
        if (name == null || pack.TryRead(name) is not { } data || !MdsFile.LooksLikeMds(data))
            return null;
        var mds = MdsFile.Parse(data);
        var meshes = _models.BuildObjectMeshes(mds, textures);
        foreach (var mesh in meshes.Values)
        {
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                if (mesh.SurfaceGetMaterial(surface) is StandardMaterial3D material)
                    material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            }
        }
        var tree = MdsModelLoader.BuildFrameTree(mds, meshes, name);
        return Write($"{packPath}/{name}", packPath, fileStem, "mds", tree, ModelDocument.KindModel);
    }
}
