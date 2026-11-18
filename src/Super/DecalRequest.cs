using System.Numerics;
using Rendition;

namespace Super;

/// <summary>A decal placed with <see cref="ViewEffects.Decal"/>: the box it fills, what it lays on and how much of each part.</summary>
public readonly record struct DecalRequest(int Key, Matrix4x4 Box, Surface Material, DecalBlend Blend);
