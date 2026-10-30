using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenCloud.Assets.Models;
using OpenCloud.Converter.Export;
using OpenCloud.Core.IO;
using OpenCloud.Game.Items;

namespace OpenCloud.Converter;

/// <summary>
/// One-time conversion of the user's Dark Cloud 2 disc into the open formats the engine reads:
/// <code>godot --headless --path opencloud-converter -- --source=&lt;disc.iso&gt; --out=&lt;folder&gt;</code>
/// <c>--source</c> can be a disc image, <c>DATA.HD4</c> or an extracted folder (repeatable; later
/// sources win). <c>--only=&lt;prefix&gt;</c> converts only the original paths that start with it;
/// <c>--zip=&lt;file&gt;</c> also packs the output folder into one ZIP the engine can mount.
/// Models become GLB with a <c>.model.json</c> companion, textures PNG with a
/// <c>.textures.json</c>, maps, skies and the item table JSON. The output folder gets an
/// <c>opencloud-data.json</c> report; the exit code is non-zero when a file failed.
/// </summary>
public partial class ConverterMain : Node
{
    private static readonly HashSet<string> TextureExtensions = new(StringComparer.Ordinal) { "img", "tm2", "im2", "im3" };

    /// <summary>Read by the model conversion of a sibling <c>.mds</c>.</summary>
    private static readonly HashSet<string> ModelSidecarExtensions = new(StringComparer.Ordinal) { "wgt", "bbp", "mot" };

    public override void _Ready()
    {
        int exitCode;
        try
        {
            exitCode = Run(OS.GetCmdlineUserArgs());
        }
        catch (Exception exception)
        {
            GD.PrintErr($"conversion failed: {exception}");
            exitCode = 2;
        }
        GetTree().Quit(exitCode);
    }

    private static int Run(string[] args)
    {
        string? Value(string name) => args.LastOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal))?[(name.Length + 3)..].Trim('"');
        var sources = args.Where(a => a.StartsWith("--source=", StringComparison.Ordinal)).Select(a => a["--source=".Length..].Trim('"')).ToList();
        string? outDir = Value("out");
        string only = VfsPath.Normalize(Value("only") ?? string.Empty);
        string? zip = Value("zip");
        if (sources.Count == 0 || outDir == null)
        {
            GD.PrintErr("usage: godot --headless --path opencloud-converter -- --source=<disc.iso|DATA.HD4|folder> --out=<folder> [--only=<path prefix>] [--zip=<data.zip>]");
            return 64;
        }

        using var retail = new RetailFileSystem();
        retail.Log += message => GD.Print($"[source] {message}");
        foreach (var source in sources)
            retail.Mount(source);

        var output = new OutputTree(outDir);
        var report = new ConversionReport();
        var models = new MdsModelLoader(retail);
        var maps = new MapExporter(retail, models, output, report);

        var files = retail.EnumerateFiles()
            .Where(path => path.StartsWith(only, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var all = new HashSet<string>(retail.EnumerateFiles(), VfsPath.Comparer);
        var consumed = new HashSet<string>(VfsPath.Comparer);

        // Maps first: they consume their texture, model list and sky packs.
        foreach (var path in files.Where(p => VfsPath.GetExtension(p) == "map"))
        {
            consumed.Add(path);
            consumed.UnionWith(MapExporter.Companions(path));
            Guard(report, path, () => maps.Export(path));
        }

        foreach (var path in files)
        {
            if (consumed.Contains(path) || IsContainer(path))
                continue;
            string extension = VfsPath.GetExtension(path);
            switch (extension)
            {
                case "sky":
                    Guard(report, path, () => maps.ExportSky(path));
                    break;
                case "chr":
                case "pcp":
                    Guard(report, path, () => ExportPack(retail, models, output, report, path));
                    break;
                case "mds":
                    Guard(report, path, () => ExportModel(models, output, report, path, models.Load(path), ModelDocument.KindModel));
                    break;
                case var t when TextureExtensions.Contains(t):
                    Guard(report, path, () =>
                    {
                        if (TextureExporter.Export(output, path, retail.ReadAllBytes(path), VfsPath.GetDirectory(path)) != null)
                            report.TextureFiles++;
                        else
                            report.Note(path, "no picture");
                    });
                    break;
                case "cfg" when VfsPath.Comparer.Equals(path, ItemCatalog.CommonDataPath):
                    Guard(report, path, () =>
                    {
                        var catalog = ItemCatalog.ParseScript(retail.ReadAllBytes(path));
                        output.WriteJson(ItemCatalog.DocumentPath, catalog.ToJson());
                        report.Note(path, $"item table: {catalog.Count} items");
                    });
                    break;
                case var s when ModelSidecarExtensions.Contains(s) && all.Contains(VfsPath.ChangeExtension(path, "mds")):
                    break;
                default:
                    report.Skip(extension.Length > 0 ? extension : "(none)");
                    break;
            }
        }

        output.WriteJson(ConversionReport.FileName, report.ToJson(retail.Sources.Select(s => s.Description)));

        foreach (var note in report.Notes)
            GD.Print($"note: {note}");
        foreach (var failure in report.Failures)
            GD.PrintErr($"failed: {failure}");
        GD.Print($"converted: {report.Characters} characters, {report.Costumes} costumes, {report.Models} model files, {report.TextureFiles} texture files, {report.Maps} maps, {report.Skies} skies");
        GD.Print($"not converted yet: {string.Join(", ", report.NotConverted.Select(p => $"{p.Key} {p.Value}"))}");
        GD.Print($"wrote {output.Files} files ({output.Bytes / 1024} KiB) to {output.Root}");
        if (zip != null)
        {
            zip = System.IO.Path.GetFullPath(zip);
            if (System.IO.File.Exists(zip))
                System.IO.File.Delete(zip);
            System.IO.Compression.ZipFile.CreateFromDirectory(output.Root, zip, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
            GD.Print($"packed {zip}");
        }
        return report.Failures.Count == 0 ? 0 : 1;
    }

    /// <summary>The disc image or file index of a mounted folder, already read as a source.</summary>
    private static bool IsContainer(string path) =>
        VfsPath.GetDirectory(path).Length == 0 && VfsPath.GetExtension(path) is "iso" or "hd4" or "dat";

    private static void Guard(ConversionReport report, string path, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is System.IO.InvalidDataException or System.IO.IOException or ArgumentException or InvalidOperationException or NotSupportedException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            report.Fail(path, exception.Message);
        }
    }

    private static void ExportPack(RetailFileSystem retail, MdsModelLoader models, OutputTree output, ConversionReport report, string path)
    {
        var pack = retail.TryOpenPack(path);
        if (pack == null)
        {
            report.Fail(path, "not a PACK archive");
            return;
        }
        string name = VfsPath.GetFileName(path);
        if (MdsModelLoader.IsCostume(pack))
        {
            ExportModel(models, output, report, path, models.LoadCostume(pack, name), ModelDocument.KindCostume);
            report.Costumes++;
            return;
        }
        var scene = models.LoadPack(pack, name);
        bool character = models.LastReport.Character != null;
        ExportModel(models, output, report, path, scene, character ? ModelDocument.KindCharacter : ModelDocument.KindModel);
        if (character)
            report.Characters++;
    }

    private static void ExportModel(MdsModelLoader models, OutputTree output, ConversionReport report, string path, Node3D scene, string kind)
    {
        try
        {
            var model = ModelExporter.Export(scene, kind, path);
            string target = output.Unique(VfsPath.GetDirectory(path), System.IO.Path.GetFileNameWithoutExtension(VfsPath.GetFileName(path)), VfsPath.GetExtension(path), ".glb");
            output.Write(target, model.Glb);
            if (model.Document.Models.Count > 0 || kind != ModelDocument.KindModel)
                output.WriteJson(ModelDocument.PathFor(target), model.Document.ToJson());
            foreach (var warning in model.Warnings)
                report.Note(path, warning);
            foreach (var note in models.LastReport.Notes)
                report.Note(path, note);
            report.Models++;
        }
        finally
        {
            scene.Free();
        }
    }
}
