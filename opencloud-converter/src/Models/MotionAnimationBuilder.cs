using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NMatrix = System.Numerics.Matrix4x4;
using NVector4 = System.Numerics.Vector4;

namespace OpenCloud.Assets.Models;

/// <summary>
/// Turns the transform key lists of a motion file into Godot <see cref="Animation"/>s that drive
/// the bones of a scene built by <see cref="MdsModelLoader"/>.
///
/// Each frame's pose is evaluated as gameutil <c>MotionProc</c> evaluates it: the key list is
/// binary searched for the last key at or before the frame, rotation keys are slerped along the
/// shorter arc (snapping straight to the next key when the two are nearly opposite) and written
/// over the frame's rotation rows, translation keys are lerped into its translation row, and
/// scale keys are lerped and applied after the rotation along the parent's axes
/// (<c>mgCFrame::GetLocalMatrix</c>). Godot composes scale before rotation, so a bone with scale
/// keys is resampled at every key of either list and decomposed; any shear that composition
/// would add is dropped.
/// </summary>
public static class MotionAnimationBuilder
{
    /// <summary>
    /// Motion frames per second: the game steps a motion once per 30 Hz game frame by its
    /// <c>KEY</c> step.
    /// </summary>
    public const float FramesPerSecond = ModelMeta.FramesPerSecond;

    /// <summary>Thresholds of the game's interpolation (MotionProc, QuatSlerp).</summary>
    private const float StartEpsilon = 0.001f;
    private const float EndEpsilon = 0.999f;
    private const float OppositeCosine = 0.01f;

    /// <summary>
    /// Builds one animation covering motion frames <paramref name="startFrame"/> to
    /// <paramref name="endFrame"/>, with keys at the motion's own key frames.
    /// </summary>
    /// <param name="lists">Key lists of the motion file.</param>
    /// <param name="skeletonPath">Path of the <see cref="Skeleton3D"/> from the animation root.</param>
    /// <param name="boneNames">Bone name of each MDS object, indexed by frame.</param>
    /// <param name="restMatrices">Authored local matrix of each frame (the pose its keys start from).</param>
    public static Animation Build(
        IReadOnlyList<MotionKeyList> lists,
        string skeletonPath,
        IReadOnlyList<string> boneNames,
        IReadOnlyList<NMatrix> restMatrices,
        int startFrame,
        int endFrame,
        bool loop)
    {
        // A KEY that ends before it starts plays as a still pose at its start.
        endFrame = Math.Max(endFrame, startFrame);
        var animation = new Animation
        {
            Length = Math.Max((endFrame - startFrame) / FramesPerSecond, 1.0f / FramesPerSecond),
            LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None,
            Step = 1.0f / FramesPerSecond,
        };

        foreach (var group in lists.Where(l => l.Keys.Length > 0).GroupBy(l => l.Frame).OrderBy(g => g.Key))
        {
            int frame = group.Key;
            if (frame < 0 || frame >= boneNames.Count)
                continue;

            var rotation = group.LastOrDefault(l => l.Type == MotionKeyType.Rotation);
            var scale = group.LastOrDefault(l => l.Type == MotionKeyType.Scale);
            var translation = group.LastOrDefault(l => l.Type == MotionKeyType.Translation);
            string path = $"{skeletonPath}:{boneNames[frame]}";

            if (translation != null)
            {
                int track = animation.AddTrack(Animation.TrackType.Position3D);
                animation.TrackSetPath(track, path);
                foreach (float time in SampleTimes(startFrame, endFrame, translation))
                {
                    var value = Lerp(translation, time);
                    animation.PositionTrackInsertKey(track, (time - startFrame) / FramesPerSecond, new Vector3(value.X, value.Y, value.Z));
                }
            }

            if (rotation != null || scale != null)
            {
                int rotationTrack = animation.AddTrack(Animation.TrackType.Rotation3D);
                animation.TrackSetPath(rotationTrack, path);
                int scaleTrack = animation.AddTrack(Animation.TrackType.Scale3D);
                animation.TrackSetPath(scaleTrack, path);

                var times = new SortedSet<float>();
                if (rotation != null)
                    times.UnionWith(SampleTimes(startFrame, endFrame, rotation));
                if (scale != null)
                    times.UnionWith(SampleTimes(startFrame, endFrame, scale));

                var rest = frame < restMatrices.Count ? restMatrices[frame] : NMatrix.Identity;
                Quaternion? previous = null;
                foreach (float time in times)
                {
                    var basis = LocalBasis(rotation, scale, rest, time);
                    var quaternion = basis.GetRotationQuaternion();
                    // Keep consecutive keys in one hemisphere so Godot's slerp takes the game's arc.
                    if (previous is { } p && p.Dot(quaternion) < 0)
                        quaternion = -quaternion;
                    previous = quaternion;

                    float seconds = (time - startFrame) / FramesPerSecond;
                    animation.RotationTrackInsertKey(rotationTrack, seconds, quaternion);
                    animation.ScaleTrackInsertKey(scaleTrack, seconds, basis.Scale);
                }
            }
        }
        return animation;
    }

    /// <summary>
    /// Times (in motion frames) at which a list must be sampled for an interval: its keys inside
    /// the interval, both ends, and just past the start of any span where the game snaps.
    /// </summary>
    private static IEnumerable<float> SampleTimes(int startFrame, int endFrame, MotionKeyList list)
    {
        yield return startFrame;
        for (int i = 0; i < list.Keys.Length; i++)
        {
            uint frame = list.Keys[i].Frame;
            if (frame > startFrame && frame < endFrame)
                yield return frame;

            if (list.Type == MotionKeyType.Rotation && i + 1 < list.Keys.Length && IsOpposite(list.Keys[i].Value, list.Keys[i + 1].Value))
            {
                float span = list.Keys[i + 1].Frame - frame;
                float snap = frame + span * StartEpsilon * 1.5f;
                if (snap > startFrame && snap < endFrame)
                    yield return snap;
            }
        }
        yield return endFrame;
    }

    /// <summary>Finds the keys around a time the way MotionProc does, with the blend factor.</summary>
    private static (int Key, int Next, float T) Locate(MotionKeyList list, float time)
    {
        var keys = list.Keys;
        uint frame = time <= 0 ? 0u : (uint)time;
        int low = 0, high = keys.Length;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (keys[middle].Frame <= frame)
                low = middle + 1;
            else
                high = middle;
        }
        // Retail data starts every list at frame 0; clamp rather than read before the list.
        int key = Math.Max(low - 1, 0);
        int next = Math.Min(key + 1, keys.Length - 1);
        float span = (float)keys[next].Frame - keys[key].Frame;
        float t = span > 0 ? (time - keys[key].Frame) / span : 1.0f;
        return (key, next, t);
    }

    private static NVector4 Lerp(MotionKeyList list, float time)
    {
        var (key, next, t) = Locate(list, time);
        if (t <= StartEpsilon)
            return list.Keys[key].Value;
        if (t >= EndEpsilon)
            return list.Keys[next].Value;
        return NVector4.Lerp(list.Keys[key].Value, list.Keys[next].Value, t);
    }

    private static NVector4 Slerp(MotionKeyList list, float time)
    {
        var (key, next, t) = Locate(list, time);
        var from = list.Keys[key].Value;
        var to = list.Keys[next].Value;
        if (t <= StartEpsilon)
            return from;
        if (t >= EndEpsilon)
            return to;

        float cosine = NVector4.Dot(from, to);
        if (cosine < 0)
        {
            to = -to;
            cosine = -cosine;
        }
        if (cosine < OppositeCosine)
            return to;

        float a, b;
        if (1.0f - cosine > OppositeCosine)
        {
            float theta = MathF.Acos(cosine);
            float inverseSine = 1.0f / MathF.Sin(theta);
            a = inverseSine * MathF.Sin((1.0f - t) * theta);
            b = inverseSine * MathF.Sin(t * theta);
        }
        else
        {
            a = 1.0f - t;
            b = t;
        }
        return from * a + to * b;
    }

    private static bool IsOpposite(NVector4 a, NVector4 b) => MathF.Abs(NVector4.Dot(a, b)) < OppositeCosine;

    /// <summary>The frame's local basis at a time, in Godot's column convention.</summary>
    private static Basis LocalBasis(MotionKeyList? rotation, MotionKeyList? scale, NMatrix rest, float time)
    {
        // Rows of the game's rotation part: from the slerped key, or the authored matrix.
        var rows = rotation != null ? QuaternionRows(Slerp(rotation, time)) : RestRows(rest);
        var s = scale != null ? Lerp(scale, time) : NVector4.One;

        // GetLocalMatrix scales column j of every row by scale[j]; Godot's basis column i is row i.
        Vector3 Row(int i) => new(rows[i].X * s.X, rows[i].Y * s.Y, rows[i].Z * s.Z);
        return new Basis(Row(0), Row(1), Row(2));
    }

    private static Vector3[] RestRows(NMatrix m) => new[]
    {
        new Vector3(m.M11, m.M12, m.M13),
        new Vector3(m.M21, m.M22, m.M23),
        new Vector3(m.M31, m.M32, m.M33),
    };

    /// <summary>
    /// The rotation rows <c>QuatToMat</c> (mg_frame) writes for a key value. The key's first
    /// component is the scalar part and the other three the vector part.
    /// </summary>
    private static Vector3[] QuaternionRows(NVector4 q)
    {
        float w = q.X, x = q.Y, y = q.Z, z = q.W;
        return new[]
        {
            new Vector3(1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)),
            new Vector3(2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)),
            new Vector3(2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)),
        };
    }
}
