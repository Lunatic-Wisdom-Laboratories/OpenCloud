using Godot;
using NVector3 = System.Numerics.Vector3;

namespace OpenCloud.Assets.Maps;

/// <summary>
/// The local matrix of a frame placed by position, rotation and scale (<c>mgCFrame::GetLocalMatrix</c>
/// with <c>use_srt</c>): the frame's own matrix is scaled (its translation
/// is not), rotated about X, then Y, then Z (translation included), then moved by the position.
/// Map parts, pieces and function points are all positioned this way; a part's frame has an
/// identity matrix of its own, a piece's is the root matrix of its model.
/// </summary>
public static class MapTransforms
{
    public static Transform3D Frame(NVector3 position, NVector3 rotation, NVector3 scale, Transform3D local)
    {
        var rotate = new Basis(Vector3.Back, rotation.Z) * new Basis(Vector3.Up, rotation.Y) * new Basis(Vector3.Right, rotation.X);
        var scaled = Basis.FromScale(new Vector3(scale.X, scale.Y, scale.Z)) * local.Basis;
        return new Transform3D(rotate * scaled, rotate * local.Origin + new Vector3(position.X, position.Y, position.Z));
    }
}
