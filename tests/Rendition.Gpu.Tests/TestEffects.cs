namespace Rendition.Gpu.Tests;

internal static class TestEffects
{
    /// <summary>
    /// Every effect except baked lighting, for tests that compare a bake against the live lighting. Point and spot lights
    /// stay on: the live lighting a missing or unread bake falls back to needs them.
    /// </summary>
    public const RenderEffects OnlyBaked = RenderEffects.All & ~(RenderEffects.BakedLighting | RenderEffects.LocalLights);
}
