using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using OpenCloud.Core;

namespace OpenCloud.Assets.Models;

/// <summary>
/// One action of a character model: a <c>KEY</c> interval of a motion slot, written as its own
/// glTF animation.
/// </summary>
/// <param name="Clip">Name of the glTF animation.</param>
/// <param name="Slot">Motion slot (<c>MOTION n</c>); the engine plays it from library <c>m&lt;slot&gt;</c>.</param>
/// <param name="Name">Clip name inside the slot's library.</param>
/// <param name="GameName">The interval's original name, which game scripts use.</param>
/// <param name="Step">Motion frames advanced per game frame.</param>
/// <param name="KeyIndex">Position of the interval in its slot's <c>KEY</c> list.</param>
public sealed record ModelAnimation(string Clip, int Slot, string Name, string GameName, float Step, int KeyIndex, bool Loop);

/// <summary>
/// A model scene inside a converted file: the frames its skeleton was built from, which of
/// them are skinned (<c>VERTEX_ANIME</c>), the base matrices skins are bound to, and its actions.
/// </summary>
public sealed class ModelPart
{
    /// <summary>Name of the scene's node.</summary>
    public required string Node { get; init; }

    /// <summary>Bone name of every frame, by frame index.</summary>
    public required string[] Frames { get; init; }

    public string[] DeformFrames { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Scene-space base matrix of every frame (the bind pose when the file has one), by frame
    /// index; empty when the model keeps its rest pose.
    /// </summary>
    public Transform3D[] BaseMatrices { get; init; } = System.Array.Empty<Transform3D>();

    public List<ModelAnimation> Animations { get; } = new();
}

/// <summary>
/// The JSON companion of a converted model (<c>&lt;name&gt;.model.json</c> beside
/// <c>&lt;name&gt;.glb</c>). glTF holds the geometry, materials, skeleton, skins and animation
/// curves; this document holds what glTF cannot say about them.
/// </summary>
public sealed class ModelDocument
{
    public const string Format = "opencloud.model";
    public const int Version = 1;
    public const string Extension = ".model.json";

    public const string KindModel = "model";
    public const string KindCharacter = "character";
    public const string KindCostume = "costume";

    /// <summary><see cref="KindModel"/>, <see cref="KindCharacter"/> or <see cref="KindCostume"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Path of the original file, for reference.</summary>
    public string Source { get; init; } = string.Empty;

    public List<ModelPart> Models { get; } = new();

    /// <summary>Nodes the game does not draw (shadow and LOD models).</summary>
    public List<string> HiddenNodes { get; } = new();

    public ModelPart? FindPart(string node) => Models.FirstOrDefault(m => m.Node == node);

    /// <summary>Companion path of a converted model: <c>a/b.glb</c> → <c>a/b.model.json</c>.</summary>
    public static string PathFor(string modelPath)
    {
        int dot = modelPath.LastIndexOf('.');
        return (dot > modelPath.LastIndexOf('/') ? modelPath[..dot] : modelPath) + Extension;
    }

    public JsonObject ToJson()
    {
        var json = JsonValues.Header(Format, Version);
        json["kind"] = Kind;
        json["source"] = Source;
        var models = new JsonArray();
        foreach (var part in Models)
        {
            var model = new JsonObject
            {
                ["node"] = part.Node,
                ["frames"] = JsonValues.Array(part.Frames),
            };
            if (part.DeformFrames.Length > 0)
                model["deform_frames"] = JsonValues.Array(part.DeformFrames);
            if (part.BaseMatrices.Length > 0)
                model["base_matrices"] = new JsonArray(part.BaseMatrices.Select(m => (JsonNode?)JsonValues.Array(m)).ToArray());
            if (part.Animations.Count > 0)
            {
                model["animations"] = new JsonArray(part.Animations.Select(a => (JsonNode?)new JsonObject
                {
                    ["clip"] = a.Clip,
                    ["slot"] = a.Slot,
                    ["name"] = a.Name,
                    ["game_name"] = a.GameName,
                    ["step"] = a.Step,
                    ["key_index"] = a.KeyIndex,
                    ["loop"] = a.Loop,
                }).ToArray());
            }
            models.Add(model);
        }
        json["models"] = models;
        if (HiddenNodes.Count > 0)
            json["hidden_nodes"] = JsonValues.Array(HiddenNodes);
        return json;
    }

    public static ModelDocument FromJson(JsonObject json, string path)
    {
        JsonValues.CheckFormat(json, Format, Version, path);
        var document = new ModelDocument
        {
            Kind = JsonValues.Str(json, "kind") ?? KindModel,
            Source = JsonValues.Str(json, "source") ?? string.Empty,
        };
        foreach (var model in JsonValues.Objects(json, "models"))
        {
            var part = new ModelPart
            {
                Node = JsonValues.Str(model, "node") ?? string.Empty,
                Frames = JsonValues.Strings(model["frames"]),
                DeformFrames = JsonValues.Strings(model["deform_frames"]),
                BaseMatrices = model["base_matrices"] is JsonArray bases ? bases.Select(JsonValues.Transform).ToArray() : System.Array.Empty<Transform3D>(),
            };
            foreach (var animation in JsonValues.Objects(model, "animations"))
            {
                part.Animations.Add(new ModelAnimation(
                    JsonValues.Str(animation, "clip") ?? string.Empty,
                    JsonValues.Int(animation, "slot"),
                    JsonValues.Str(animation, "name") ?? string.Empty,
                    JsonValues.Str(animation, "game_name") ?? string.Empty,
                    JsonValues.Float(animation, "step", 1.0f),
                    JsonValues.Int(animation, "key_index"),
                    JsonValues.Bool(animation, "loop", true)));
            }
            document.Models.Add(part);
        }
        document.HiddenNodes.AddRange(JsonValues.Strings(json["hidden_nodes"]));
        return document;
    }
}
