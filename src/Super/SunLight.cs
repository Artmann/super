using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// A light as far away as the sun, shining the same way everywhere. One sun casts the
/// shadows: the one marked with <see cref="CastsShadows"/>, or else the first.
/// </summary>
public readonly record struct SunLight(Vector3 Direction, Vector3 Color, bool CastsShadows = false, LightMode Mode = LightMode.Mixed);
