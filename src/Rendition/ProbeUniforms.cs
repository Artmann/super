using System.Numerics;
using Rendition.Passes;

namespace Rendition;

/// <summary>Writes where the frame's light probes are, live and baked, into its uniforms.</summary>
internal static unsafe class ProbeUniforms
{
    /// <summary>Where the live light probes are: the fixed grid's box and the cascades round the camera.</summary>
    public static void WriteLive(FrameUniforms* uniforms, in FrameView view, LightProbes probes)
    {
        // The fixed grid, read wherever it has a probe updated.
        bool inUse = probes.InUse;
        if (inUse && view.LightProbes is { } lightProbes)
        {
            var (x, y, z) = ProbeGrid.Counts(lightProbes);
            uniforms->ProbeBlock.LightProbeMin = new Vector4(lightProbes.Min, lightProbes.Spacing);
            uniforms->ProbeBlock.LightProbeCounts = new Vector4(x, y, z, 1f);
        }
        else
        {
            uniforms->ProbeBlock.LightProbeMin = Vector4.Zero;
            uniforms->ProbeBlock.LightProbeCounts = Vector4.Zero;
        }

        // The cascades, read once any of their probes holds an update.
        var cascades = probes.Batch.Cascades;
        for (int c = 0; c < ProbeCascades.Count; c++)
        {
            var origin = cascades.Origin(c);
            uniforms->ProbeBlock.ProbeCascadeOrigins[c] = new Vector4(origin.X, origin.Y, origin.Z, cascades.Layout(c).Spacing);
        }

        bool readCascades = inUse && probes.Volume.Atlases.CascadesWritten;
        var cascadeLayout = cascades.Layout(0);
        uniforms->ProbeBlock.ProbeCascadeCounts = new Vector4(
            cascadeLayout.CountX, cascadeLayout.CountY, cascadeLayout.CountZ, readCascades ? 1f : 0f);
    }

    /// <summary>
    /// The baked space's probes and box, and how far its bake has faded in. With <paramref name="band"/> off, for a
    /// test, the box is a hard edge.
    /// </summary>
    public static void WriteBaked(FrameUniforms* uniforms, FrameBakes bakes, bool band)
    {
        // Inside the baked space's box, a fresh bake's probes give the soft light, blended into the live light over one
        // probe spacing in from the box's faces, or, with the band off for a test, cut off hard at them. The
        // shader's box is the probe grid's Min..Max, which is the space's box: the space's box is in the bake's input
        // hash, so a fresh bake's grid matches it.
        if (bakes.SpaceProbes is { } baked)
        {
            var (x, y, z) = ProbeGrid.Counts(baked.Settings);
            uniforms->BakedBlock.BakedProbeMin = new Vector4(baked.Settings.Min, baked.Settings.Spacing);
            uniforms->BakedBlock.BakedProbeCounts = new Vector4(x, y, z, band ? 1f : 2f);
            uniforms->BakedBlock.BakedSpaceMin = new Vector4(baked.Settings.Min, baked.Irradiance);
            uniforms->BakedBlock.BakedSpaceMax = new Vector4(baked.Settings.Max, baked.Depth);
        }
        else
        {
            uniforms->BakedBlock.BakedProbeMin = Vector4.Zero;
            uniforms->BakedBlock.BakedProbeCounts = Vector4.Zero;
            uniforms->BakedBlock.BakedSpaceMin = Vector4.Zero;
            uniforms->BakedBlock.BakedSpaceMax = Vector4.Zero;
        }

        // How far the space's bake has faded in, which weighs its lightmaps and probes against the live light.
        uniforms->BakedBlock.BakedFade = new Vector4(bakes.SpaceFade, 0f, 0f, 0f);
    }
}
