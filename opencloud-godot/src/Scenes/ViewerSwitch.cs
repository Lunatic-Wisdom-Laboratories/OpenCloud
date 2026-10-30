using Godot;

namespace OpenCloud.Scenes;

/// <summary>
/// Tab cycles the debug scenes: asset viewer, map viewer, town, and back to the asset viewer.
/// The map viewer hands its current map to the town, so Tab walks the map being looked at.
/// </summary>
public static class ViewerSwitch
{
    public const string AssetViewer = "res://src/Scenes/AssetViewer.tscn";
    public const string MapViewer = "res://src/Scenes/MapViewer.tscn";
    public const string Town = "res://src/Scenes/TownScene.tscn";

    /// <summary>Map the next scene should open instead of <c>--map=</c>; taken once.</summary>
    private static string? _pendingMap;

    /// <summary>
    /// Marks the key handled, then changes scene. The order matters: the change takes the
    /// current scene out of the tree at once, after which it has no viewport.
    /// </summary>
    public static void Next(Node from, string scene, string? map = null)
    {
        from.GetViewport().SetInputAsHandled();
        _pendingMap = map;
        from.GetTree().ChangeSceneToFile(scene);
    }

    /// <summary>The map handed over by the previous scene, else <paramref name="fallback"/>.</summary>
    public static string TakeMap(string fallback)
    {
        string? map = _pendingMap;
        _pendingMap = null;
        return string.IsNullOrEmpty(map) ? fallback : map;
    }
}
