using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using OpenCloud.Core.IO;

namespace OpenCloud.Core;

/// <summary>
/// Finds the user's game data. Nothing from the game ships with the engine: the user points it
/// at their own <c>data.zip</c>, disc image, <c>DATA.HD4</c> or extracted folder through, in
/// order of priority:
/// <list type="number">
/// <item>command-line user arguments <c>-- --data=&lt;path&gt;</c> (repeatable);</item>
/// <item>the <c>OPENCLOUD_DATA</c> environment variable (paths separated by the OS path separator);</item>
/// <item><c>user://opencloud.cfg</c>, key <c>data/paths</c> (an array of paths);</item>
/// <item>a <c>data</c> folder or <c>data.zip</c> next to the executable or project.</item>
/// </list>
/// Each path is mounted with <see cref="VirtualFileSystem.MountAuto"/>; when several are given,
/// later ones override earlier ones.
/// </summary>
public static class GameDataPaths
{
    public const string ConfigPath = "user://opencloud.cfg";
    public const string EnvironmentVariable = "OPENCLOUD_DATA";

    public static List<string> Resolve(IEnumerable<string>? extra = null)
    {
        var paths = new List<string>();
        if (extra != null)
            paths.AddRange(extra);

        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--data=", StringComparison.Ordinal))
                paths.Add(argument["--data=".Length..].Trim('"'));
        }
        if (paths.Count > 0)
            return paths;

        var environment = System.Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environment))
        {
            paths.AddRange(environment.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return paths;
        }

        var config = new ConfigFile();
        if (config.Load(ConfigPath) == Error.Ok)
        {
            foreach (var path in config.GetValue("data", "paths", Array.Empty<string>()).AsStringArray())
                paths.Add(path);
            if (paths.Count > 0)
                return paths;
        }

        foreach (var root in new[] { OS.GetExecutablePath().GetBaseDir(), ProjectSettings.GlobalizePath("res://") })
        {
            if (string.IsNullOrEmpty(root))
                continue;
            foreach (var name in new[] { "data", "data.zip" })
            {
                string candidate = System.IO.Path.Combine(root, name);
                if (Directory.Exists(candidate) || File.Exists(candidate))
                    return new List<string> { candidate };
            }
        }
        return paths;
    }

    /// <summary>Saves the paths to the user config so later runs find the data without arguments.</summary>
    public static void Save(IReadOnlyList<string> paths)
    {
        var config = new ConfigFile();
        config.Load(ConfigPath);
        config.SetValue("data", "paths", new Godot.Collections.Array<string>(paths));
        config.Save(ConfigPath);
    }

    /// <summary>Mounts every resolved path, reporting failures instead of throwing.</summary>
    public static VirtualFileSystem MountAll(IReadOnlyList<string> paths, List<string> errors)
    {
        var vfs = new VirtualFileSystem();
        vfs.Log += message => GD.Print($"[VFS] {message}");
        foreach (var path in paths)
        {
            try
            {
                vfs.MountAuto(path);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                errors.Add($"{path}: {exception.Message}");
            }
        }
        return vfs;
    }
}
