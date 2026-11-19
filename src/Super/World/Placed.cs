using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// A material laid onto whatever lies inside the entity's box, as a component, for <see cref="ViewEffects.Decal"/>. The
/// box is the cube from −0.5 to 0.5 moved by the entity's <see cref="Transform"/> and <see cref="Parent"/>, and the
/// material projects down its −Y, so point the entity's Y out of the wall or up from the floor.
/// </summary>
public readonly record struct Decal(Surface Material, DecalBlend? Blend = null);

/// <summary>
/// A shape filled with fog of its own, as a component, for <see cref="ViewEffects.FogVolume"/>: the unit
/// <paramref name="Shape"/> moved by the entity's <see cref="Transform"/> and <see cref="Parent"/>.
/// </summary>
public readonly record struct FogVolume(FogShape Shape, FogVolumeSettings Settings);

/// <summary>
/// A reflection probe, as a component, for <see cref="ViewIndirect.ReflectionProbe"/>. It's photographed from where the
/// entity stands, and its box runs from <paramref name="BoxMin"/> to <paramref name="BoxMax"/> measured from there,
/// along the world's axes: the entity's turn and scale don't change it, so the box must hold the entity's place.
/// </summary>
public readonly record struct ReflectionCapture(
    Vector3 BoxMin,
    Vector3 BoxMax,
    float Blend = 0.5f,
    float Priority = 0f,
    ProbeUpdate Update = ProbeUpdate.OnChange,
    int Version = 0);

/// <summary>
/// A piece of the world baked once and reused wherever it stands, as a component, for
/// <see cref="ViewIndirect.BakedChunk"/>. The entity's <see cref="Transform"/> and <see cref="Parent"/> place it, and
/// may only turn and move it; <paramref name="Min"/> to <paramref name="Max"/> is its box in its own space.
/// </summary>
public readonly record struct BakedPiece(
    string Name,
    Vector3 Min,
    Vector3 Max,
    int TexelsPerMetre = BakeSettings.DefaultTexelsPerMetre,
    float EdgeBlend = Rendition.BakedChunk.DefaultEdgeBlend);
