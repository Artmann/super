using Rendition;

namespace Super;

/// <summary>How the switches of <see cref="Graphics"/> reach the renderer: each one names the effect it leaves out.</summary>
internal static class GraphicsSwitches
{
    /// <summary>Each switch, with the effect the renderer leaves out while it's off.</summary>
    private static readonly (Func<Graphics, bool> On, RenderEffects Effect)[] Switches =
    [
        (graphics => graphics.Shadowing.SunShadows, RenderEffects.SunShadows),
        (graphics => graphics.Shadowing.ContactShadows, RenderEffects.ContactShadows),
        (graphics => graphics.Lighting.LightProbes, RenderEffects.LightProbes),
        (graphics => graphics.Shadowing.LightShadows, RenderEffects.LightShadows),
        (graphics => graphics.Lighting.AmbientOcclusion, RenderEffects.AmbientOcclusion),
        (graphics => graphics.Lighting.ScreenBounce, RenderEffects.ScreenBounce),
        (graphics => graphics.Lighting.OcclusionHistory, RenderEffects.OcclusionHistory),
        (graphics => graphics.Lighting.MultiscatterSpecular, RenderEffects.MultiscatterSpecular),
        (graphics => graphics.Lighting.Reflections, RenderEffects.Reflections),
        (graphics => graphics.Screen.AntiAliasing, RenderEffects.AntiAliasing),
        (graphics => graphics.Picture.Bloom, RenderEffects.Bloom),
        (graphics => graphics.Atmosphere.Fog, RenderEffects.Fog),
        (graphics => graphics.Picture.AutoExposure, RenderEffects.AutoExposure),
        (graphics => graphics.Picture.DepthOfField, RenderEffects.DepthOfField),
        (graphics => graphics.Surfaces.Decals, RenderEffects.Decals),
        (graphics => graphics.Atmosphere.VolumetricFog, RenderEffects.VolumetricFog),
        (graphics => graphics.Surfaces.Puddles, RenderEffects.Puddles),
        (graphics => graphics.Surfaces.Wind, RenderEffects.Wind),
        (graphics => graphics.Baking.BakedLighting, RenderEffects.BakedLighting),
        (graphics => graphics.Surfaces.Materials, RenderEffects.Materials),
        (graphics => graphics.Surfaces.WearAndDirt, RenderEffects.WearAndDirt),
        (graphics => graphics.Surfaces.HeightMaps, RenderEffects.HeightMaps),
        (graphics => graphics.Lighting.Specular, RenderEffects.Specular),
        (graphics => graphics.Lighting.EnvironmentLight, RenderEffects.EnvironmentLight),
        (graphics => graphics.Lighting.Emissive, RenderEffects.Emissive),
        (graphics => graphics.Picture.Vignette, RenderEffects.Vignette),
        (graphics => graphics.Picture.Grain, RenderEffects.Grain),
        (graphics => graphics.Picture.Sharpening, RenderEffects.Sharpening),
        (graphics => graphics.Picture.ColorGrading, RenderEffects.ColorGrading),
        (graphics => graphics.Picture.ColorLut, RenderEffects.ColorLut),
        (graphics => graphics.Picture.ChromaticAberration, RenderEffects.ChromaticAberration),
        (graphics => graphics.Picture.SpeedBlur, RenderEffects.SpeedBlur),
        (graphics => graphics.Picture.Flashes, RenderEffects.Flashes),
        (graphics => graphics.Picture.ScreenEffects, RenderEffects.ScreenEffects),
        (graphics => graphics.Picture.LensFlare, RenderEffects.LensFlare),
        (graphics => graphics.Drawing.Culling, RenderEffects.Culling),
        (graphics => graphics.Drawing.SimplerModels, RenderEffects.SimplerModels),
        (graphics => graphics.Drawing.DepthFirst, RenderEffects.DepthFirst),
        (graphics => graphics.Drawing.Fades, RenderEffects.Fades),
        (graphics => graphics.Surfaces.Subsurface, RenderEffects.Subsurface),
        (graphics => graphics.Surfaces.Particles, RenderEffects.Particles),
        (graphics => graphics.Lighting.LocalLights, RenderEffects.LocalLights),
        (graphics => graphics.Atmosphere.Haze, RenderEffects.Haze),
        (graphics => graphics.Atmosphere.AerialPerspective, RenderEffects.AerialPerspective),
        (graphics => graphics.Atmosphere.Stars, RenderEffects.Stars),
        (graphics => graphics.Atmosphere.Underwater, RenderEffects.Underwater),
        (graphics => graphics.Atmosphere.FogVolumes, RenderEffects.FogVolumes),
    ];

    /// <summary>The effects the renderer leaves out for these settings.</summary>
    public static RenderEffects Off(Graphics graphics)
    {
        var off = RenderEffects.None;
        foreach (var (on, effect) in Switches)
        {
            if (!on(graphics))
            {
                off |= effect;
            }
        }

        return off;
    }
}
