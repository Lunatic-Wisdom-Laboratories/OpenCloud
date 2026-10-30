using System;
using System.Collections.Generic;

namespace OpenCloud.Assets.Models;

/// <summary>
/// The bones and blend weights of every vertex position of one skinned MDT model, built the way
/// <c>mgCVisualMotionMDT::CreateVertexWeight</c> builds them (ps2 visualmotion): a model has at
/// most 32 bones and each vertex at most four influences.
/// </summary>
public sealed class VertexWeights
{
    public const int MaxBones = 32;
    public const int MaxInfluences = 4;

    /// <summary>Frame (MDS object) index of each bone slot.</summary>
    public required IReadOnlyList<int> Bones { get; init; }

    /// <summary>Bone slot of each influence, <see cref="MaxInfluences"/> per vertex.</summary>
    public required int[] BoneSlots { get; init; }

    /// <summary>Weight of each influence, <see cref="MaxInfluences"/> per vertex.</summary>
    public required float[] Weights { get; init; }

    public int VertexCount => Weights.Length / MaxInfluences;

    /// <summary>
    /// Builds the weights of the model owned by <paramref name="modelFrame"/> from the weight
    /// lists of its scene (a <c>.wgt</c> file: one list per model frame and bone, each key a
    /// vertex position index with its weight in percent). Returns null when no list belongs to
    /// the frame or, as in the game, when the model would need more than 32 bones.
    /// </summary>
    public static VertexWeights? Build(IEnumerable<MotionKeyList> lists, int modelFrame, int vertexCount)
    {
        var bones = new List<int>();
        var slots = new int[vertexCount * MaxInfluences];
        var weights = new float[vertexCount * MaxInfluences];
        bool any = false;

        foreach (var list in lists)
        {
            if (list.Frame != modelFrame || list.Keys.Length == 0)
                continue;
            any = true;

            int slot = bones.IndexOf(list.Target);
            if (slot < 0)
            {
                if (bones.Count == MaxBones)
                    return null;
                slot = bones.Count;
                bones.Add(list.Target);
            }

            foreach (var key in list.Keys)
            {
                if (key.Frame >= (uint)vertexCount)
                    continue;
                int at = (int)key.Frame * MaxInfluences;

                // The first free influence (weight still zero) takes the bone.
                int influence = 0;
                while (influence < MaxInfluences && weights[at + influence] != 0.0f)
                    influence++;
                if (influence < MaxInfluences)
                {
                    slots[at + influence] = slot;
                    weights[at + influence] = key.Value.X / 100.0f;
                }
                else
                {
                    // A fifth influence is dropped and the four kept are renormalised.
                    Normalise(weights, at);
                }
            }
        }

        if (!any)
            return null;

        for (int at = 0; at < weights.Length; at += MaxInfluences)
        {
            float total = weights[at] + weights[at + 1] + weights[at + 2] + weights[at + 3];
            // Only sums below one are scaled up; a vertex no bone weights follows the first slot.
            if (total < 1.0f && total > 0.0f)
                Normalise(weights, at);
            else if (total == 0.0f)
                weights[at] = 1.0f;
        }

        return new VertexWeights { Bones = bones, BoneSlots = slots, Weights = weights };
    }

    private static void Normalise(float[] weights, int at)
    {
        float total = weights[at] + weights[at + 1] + weights[at + 2] + weights[at + 3];
        if (total == 0)
            return;
        for (int i = 0; i < MaxInfluences; i++)
            weights[at + i] /= total;
    }
}
