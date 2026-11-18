using System.Numerics;
using Rendition.Passes;

namespace Rendition;

/// <summary>What the last frame's uniforms gave, for tests.</summary>
internal sealed class LastFrame
{
    private readonly DirectionalLight[] _lights = new DirectionalLight[FrameUniforms.MaxLights];

    /// <summary>The sun's disc the last frame drawn from the camera wrote, leaving out probe faces.</summary>
    public Vector4 SunDisc { get; private set; }

    /// <summary>The flat soft light and reflection, from any frame, probe faces too.</summary>
    public (Vector3 Irradiance, Vector3 Reflection) Flat { get; set; }

    /// <summary>A light the last frame drawn from the camera wrote, after the air's tint.</summary>
    public DirectionalLight Light(int index) => _lights[index];

    /// <summary>Keeps the disc and the lights of a frame drawn from the camera.</summary>
    public void FromCamera(Vector4 disc, ReadOnlySpan<DirectionalLight> lights)
    {
        SunDisc = disc;
        lights[..Math.Min(lights.Length, _lights.Length)].CopyTo(_lights);
    }
}
