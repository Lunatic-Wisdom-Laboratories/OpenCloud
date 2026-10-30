using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenCloud.Assets.Models;

/// <summary>
/// What an MDS scene needs to skin its models: the weight lists of its <c>.wgt</c> file, which
/// frames are skinned and the bind pose (mg_dataset <c>CreateFrameVisual</c>,
/// character <c>_MODEL</c>/<c>_MOTION</c>).
/// </summary>
public sealed class SkinningData
{
    /// <summary>Weight lists: list frame = model frame, target = bone frame, keys = vertex/percent.</summary>
    public required IReadOnlyList<MotionKeyList> WeightLists { get; init; }

    /// <summary>
    /// Names of the frames whose models are skinned (<c>VERTEX_ANIME</c>), compared as
    /// <c>mgFrameNameComp</c> does (up to "--"); null skins every frame that has weights.
    /// </summary>
    public IReadOnlyCollection<string>? SkinnedFrames { get; init; }

    /// <summary>Scene-space bind matrix of each frame from a <c>.bbp</c>; null uses the rest pose.</summary>
    public IReadOnlyList<Matrix4x4>? BindPose { get; init; }

    public VertexWeights? WeightsFor(MdsObject frame, int vertexCount)
    {
        if (SkinnedFrames != null)
        {
            bool listed = false;
            foreach (string name in SkinnedFrames)
                listed |= string.Equals(MdsObject.StripAttributes(name), frame.BaseName, StringComparison.Ordinal);
            if (!listed)
                return null;
        }
        return VertexWeights.Build(WeightLists, frame.Index, vertexCount);
    }
}
