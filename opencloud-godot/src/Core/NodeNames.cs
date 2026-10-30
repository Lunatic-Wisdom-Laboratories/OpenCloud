using System;

namespace OpenCloud.Core;

/// <summary>Names of game frames and the Godot nodes built for them.</summary>
public static class NodeNames
{
    /// <summary>Replaces the characters Godot reserves in node names.</summary>
    public static string Safe(string name)
    {
        foreach (char c in ".:@/\"%")
            name = name.Replace(c, '_');
        return name.Length == 0 ? "Node" : name;
    }

    /// <summary>A frame name without the attribute text after "--" (<c>mgFrameNameComp</c>).</summary>
    public static string StripAttributes(string name)
    {
        int attributes = name.IndexOf("--", StringComparison.Ordinal);
        return attributes < 0 ? name : name[..attributes];
    }

    /// <summary>
    /// Name of the node holding the skinned mesh of frame <paramref name="frame"/>. The glTF
    /// importer makes node names unique by appending digits, so the index sits between markers.
    /// </summary>
    public static string SkinnedMesh(int frame) => $"skin-{frame}-x";

    /// <summary>The frame index of a <see cref="SkinnedMesh"/> name, or -1.</summary>
    public static int SkinnedMeshFrame(string name)
    {
        if (!name.StartsWith("skin-", StringComparison.Ordinal))
            return -1;
        int end = name.IndexOf("-x", 5, StringComparison.Ordinal);
        return end > 5 && int.TryParse(name.AsSpan(5, end - 5), out int frame) ? frame : -1;
    }
}
