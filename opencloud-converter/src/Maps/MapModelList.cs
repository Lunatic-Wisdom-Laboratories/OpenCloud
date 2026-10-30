using System;
using System.Collections.Generic;
using OpenCloud.Core.IO;
using OpenCloud.Core.Scripts;

namespace OpenCloud.Assets.Maps;

/// <summary>
/// A PCP model list (<c>CMdsList::LoadPCPFile</c>): a PACK of MDS models and character packs
/// whose <c>info.cfg</c> names each model and says what it is. Map pieces find their model by
/// name across all of a map's lists.
/// </summary>
public sealed class MapModelList
{
    /// <summary><c>pcp_tag</c> in retail order; binary scripts index it.</summary>
    public static readonly IReadOnlyList<string> Tags = new[] { "MDS", "TYPE", "FAR_CLIP", "MDS_END" };

    public required string Name { get; init; }
    public required PackArchive Pack { get; init; }
    public List<MapModelEntry> Entries { get; } = new();

    public MapModelEntry? Find(string name) =>
        Entries.Find(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public static MapModelList Parse(string name, PackArchive pack)
    {
        var list = new MapModelList { Name = name, Pack = pack };
        var script = pack.TryRead("info.cfg");
        if (script == null)
            return list;

        // The list holds as many entries as the pack has mds and chr members.
        int capacity = 0;
        foreach (var _ in pack.WithExtension("mds"))
            capacity++;
        foreach (var _ in pack.WithExtension("chr"))
            capacity++;

        string? current = null;
        var type = MapModelType.Model;
        float farDistance = -1.0f;
        int farFade = 0;
        foreach (var command in GameScript.Parse(script, Tags))
        {
            switch (command.Tag)
            {
                case "MDS":
                    current = null;
                    if (list.Entries.Count >= capacity)
                        break;
                    var model = command.Arg(0).AsString;
                    // A name already listed is refused ("same mds").
                    if (model != null && list.Find(model) != null)
                        break;
                    current = model ?? string.Empty;
                    type = MapModelType.Model;
                    farDistance = -1.0f;
                    farFade = 0;
                    break;
                case "TYPE" when current != null:
                    type = command.Arg(0).AsInt switch
                    {
                        0 => MapModelType.Model,
                        1 => MapModelType.Collision,
                        2 => MapModelType.CameraCollision,
                        3 or 4 => MapModelType.Character,
                        var other => (MapModelType)other,
                    };
                    break;
                case "FAR_CLIP" when current != null:
                    farDistance = command.Arg(0).AsFloat;
                    farFade = command.Arg(1).AsInt;
                    break;
                case "MDS_END" when current != null:
                    // pcpMDS_END fails, leaving the slot without a frame, when the pack lacks the file.
                    if (pack.Contains(current))
                        list.Entries.Add(new MapModelEntry(current, type, farDistance, farFade, string.Empty));
                    current = null;
                    break;
            }
        }
        return list;
    }
}
