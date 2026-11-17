using Rendition;

namespace Super;

/// <summary>How the switches of <see cref="Graphics"/> reach the renderer: each one names the effect it leaves out.</summary>
public sealed partial class Graphics
{
    /// <summary>The effects the renderer leaves out.</summary>
    internal RenderEffects Off
    {
        get
        {
            var off = RenderEffects.None;
            foreach (var (on, effect) in Switches)
            {
                if (!on(this))
                {
                    off |= effect;
                }
            }

            return off;
        }
    }

    /// <summary>Each switch, with the effect the renderer leaves out while it's off.</summary>
    private static readonly (Func<Graphics, bool> On, RenderEffects Effect)[] Switches =
    [
        (graphics => graphics.SunShadows, RenderEffects.SunShadows),
        (graphics => graphics.ContactShadows, RenderEffects.ContactShadows),
        (graphics => graphics.LightProbes, RenderEffects.LightProbes),
        (graphics => graphics.LightShadows, RenderEffects.LightShadows),
        (graphics => graphics.AmbientOcclusion, RenderEffects.AmbientOcclusion),
        (graphics => graphics.ScreenBounce, RenderEffects.ScreenBounce),
        (graphics => graphics.OcclusionHistory, RenderEffects.OcclusionHistory),
        (graphics => graphics.MultiscatterSpecular, RenderEffects.MultiscatterSpecular),
        (graphics => graphics.Reflections, RenderEffects.Reflections),
        (graphics => graphics.AntiAliasing, RenderEffects.AntiAliasing),
        (graphics => graphics.Bloom, RenderEffects.Bloom),
        (graphics => graphics.Fog, RenderEffects.Fog),
        (graphics => graphics.AutoExposure, RenderEffects.AutoExposure),
        (graphics => graphics.DepthOfField, RenderEffects.DepthOfField),
        (graphics => graphics.Decals, RenderEffects.Decals),
        (graphics => graphics.VolumetricFog, RenderEffects.VolumetricFog),
        (graphics => graphics.Puddles, RenderEffects.Puddles),
        (graphics => graphics.Wind, RenderEffects.Wind),
        (graphics => graphics.BakedLighting, RenderEffects.BakedLighting),
        (graphics => graphics.Materials, RenderEffects.Materials),
        (graphics => graphics.WearAndDirt, RenderEffects.WearAndDirt),
        (graphics => graphics.HeightMaps, RenderEffects.HeightMaps),
        (graphics => graphics.Specular, RenderEffects.Specular),
        (graphics => graphics.EnvironmentLight, RenderEffects.EnvironmentLight),
        (graphics => graphics.Emissive, RenderEffects.Emissive),
        (graphics => graphics.Vignette, RenderEffects.Vignette),
        (graphics => graphics.Grain, RenderEffects.Grain),
        (graphics => graphics.Sharpening, RenderEffects.Sharpening),
        (graphics => graphics.ColorGrading, RenderEffects.ColorGrading),
        (graphics => graphics.Culling, RenderEffects.Culling),
        (graphics => graphics.SimplerModels, RenderEffects.SimplerModels),
        (graphics => graphics.DepthFirst, RenderEffects.DepthFirst),
        (graphics => graphics.Fades, RenderEffects.Fades),
        (graphics => graphics.Subsurface, RenderEffects.Subsurface),
        (graphics => graphics.Particles, RenderEffects.Particles),
        (graphics => graphics.LocalLights, RenderEffects.LocalLights),
        (graphics => graphics.Haze, RenderEffects.Haze),
        (graphics => graphics.AerialPerspective, RenderEffects.AerialPerspective),
        (graphics => graphics.Stars, RenderEffects.Stars),
        (graphics => graphics.Underwater, RenderEffects.Underwater),
        (graphics => graphics.FogVolumes, RenderEffects.FogVolumes),
    ];
}
