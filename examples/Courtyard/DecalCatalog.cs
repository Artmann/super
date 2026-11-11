using System.Numerics;
using Rendition;

namespace Courtyard;

/// <summary>
/// A list of hand-placed decals, one file in content/decals. The Courtyard's is decals/courtyard.json: the dirt round
/// the barrel, the wheel, the crates and the sacks, rust run down from the barrel's hoops, moss, wet ground and a trail of
/// boot prints. The grime on the wall stays in <see cref="WallStains"/> and the gravel in <see cref="GravelSpills"/>.
/// </summary>
/// <param name="Id">The list's name, the file's own.</param>
/// <param name="Decals">Its decals, drawn in this order.</param>
public sealed record DecalList(string Id, IReadOnlyList<PlacedDecal> Decals);

/// <summary>
/// One decal, in the game's axes: Blender's (x, y, z) from art/courtyard.py is (x, z, −y) here.
/// </summary>
/// <param name="Name">A name for it, unique in its list.</param>
/// <param name="Material">The cooked material it is laid with, such as <c>materials/stain.material.cs</c>.</param>
/// <param name="Position">The middle of its box.</param>
/// <param name="Size">
/// The box's width across the image, its length from the image's V 0 edge to its V 1 edge, and its depth along
/// <paramref name="Facing"/>, in metres.
/// </param>
/// <param name="Facing">The way it faces, out of the surface it lies on: up when left out.</param>
/// <param name="Turn">
/// Degrees about <paramref name="Facing"/>. At 0 the image's V 0 edge points up a wall, or towards the brick wall (−Z)
/// on the ground; a positive turn is anticlockwise seen from in front.
/// </param>
/// <param name="Color">How much of its colour reaches the surface, 0 to 1.</param>
/// <param name="Normal">How much of its bumps reach the surface, 0 to 1.</param>
/// <param name="Roughness">How much of its roughness replaces the surface's, 0 to 1.</param>
public sealed record PlacedDecal(
    string Name,
    string Material,
    Vector3 Position,
    Vector3 Size,
    Vector3? Facing = null,
    float Turn = 0f,
    float Color = 1f,
    float Normal = 1f,
    float Roughness = 1f)
{
    /// <summary>
    /// The decal's box, for <c>View.Decal</c>: the cube from −0.5 to 0.5 with its X across the image, its Y along
    /// <see cref="Facing"/> and its Z towards the image's V 0 edge, scaled by <see cref="Size"/> and moved to
    /// <see cref="Position"/>.
    /// </summary>
    public Matrix4x4 Box()
    {
        var facing = Vector3.Normalize(Facing ?? Vector3.UnitY);
        // Up a wall, or towards the brick wall on the ground, laid flat into the decal's own plane.
        var reference = MathF.Abs(facing.Y) > 0.9f ? -Vector3.UnitZ : Vector3.UnitY;
        var solid = Vector3.Normalize(reference - facing * Vector3.Dot(reference, facing));
        solid = Vector3.Transform(solid, Quaternion.CreateFromAxisAngle(facing, Turn * MathF.PI / 180f));
        // +X is facing × solid, as GravelSpills' boxes are, so the image isn't mirrored.
        var across = Vector3.Cross(facing, solid) * Size.X;
        var depth = facing * Size.Z;
        var along = solid * Size.Y;
        return new Matrix4x4(
            across.X, across.Y, across.Z, 0f,
            depth.X, depth.Y, depth.Z, 0f,
            along.X, along.Y, along.Z, 0f,
            Position.X, Position.Y, Position.Z, 1f);
    }

    /// <summary>How much of the material reaches the surface.</summary>
    public DecalBlend Blend => new(Color, Normal, Roughness);
}
