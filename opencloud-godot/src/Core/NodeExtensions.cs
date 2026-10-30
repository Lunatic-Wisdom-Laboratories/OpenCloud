using Godot;

namespace OpenCloud.Core;

public static class NodeExtensions
{
    /// <summary>Sets a meta value and returns the node, for use in object initialisers.</summary>
    public static T WithMeta<T>(this T node, StringName name, Variant value) where T : GodotObject
    {
        node.SetMeta(name, value);
        return node;
    }
}
