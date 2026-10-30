namespace OpenCloud.Assets.Models;

/// <summary>Node names and metadata keys the model loaders put on the scenes they build.</summary>
public static class ModelMeta
{
    /// <summary>One motion frame: the game steps motions at 30 frames a second.</summary>
    public const float FramesPerSecond = 30.0f;

    /// <summary>Name of the skeleton node inside every model scene.</summary>
    public const string SkeletonNodeName = "Skeleton";

    /// <summary>Name of the <see cref="Godot.AnimationPlayer"/> of an animated model.</summary>
    public const string AnimationPlayerName = "AnimationPlayer";

    /// <summary>On a model: the bone name of each frame, by frame index.</summary>
    public const string BoneNames = "opencloud_bone_names";

    /// <summary>On a model's node (mesh or bone attachment): the frame that owns it.</summary>
    public const string Frame = "opencloud_frame";

    /// <summary>On a character model: names of its deform (<c>VERTEX_ANIME</c>) frames.</summary>
    public const string DeformFrames = "opencloud_deform_frames";

    /// <summary>On a model: scene-space base matrix (Transform3D) of every frame.</summary>
    public const string BaseMatrices = "opencloud_base_matrices";

    /// <summary>On a clip: the original name of its <c>KEY</c> interval.</summary>
    public const string GameName = "game_name";

    /// <summary>On a clip: motion frames advanced per game frame.</summary>
    public const string Step = "step";

    /// <summary>On a clip: its position in its slot's <c>KEY</c> list (players list clips by name).</summary>
    public const string KeyIndex = "key_index";
}
