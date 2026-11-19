using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The renderer's materials and surface shaders: checked before they're made or changed, and let go of two frames
/// after they're destroyed, along with the structures rays are traced through that name them. Part of
/// <see cref="RendererAssets"/>.
/// </summary>
internal sealed class MaterialAssets(
    (VulkanContext Context, ResourceTable Resources, RetiredResources Retired, RayTracing? Tracing) gpu,
    Action<Func<BakeJob, bool>> stopBakesThat,
    GBufferPass shaders,
    TextureAssets textures)
{
    /// <summary>See <see cref="RendererAssets.CreateMaterial"/>.</summary>
    public MaterialHandle Create(in Material material)
    {
        MaterialChecks.Check(material, gpu.Resources, shaders.ShaderCount);
        return gpu.Resources.Materials.Add(material);
    }

    /// <summary>See <see cref="RendererAssets.UpdateMaterial"/>.</summary>
    public void Update(MaterialHandle handle, in Material material)
    {
        MaterialChecks.Check(material, gpu.Resources, shaders.ShaderCount);
        var materials = gpu.Resources.Materials;
        // A destroyed material's structures are already gone, so only a live one is checked.
        bool traced = gpu.Tracing is not null && materials.IsLive(handle);
        var opaqueBefore = traced ? TraceEncoding.GeometryFlagsFor(materials[handle].Cutouts, materials[handle].Flags) : default;
        materials.Update(handle, material);
        if (traced && TraceEncoding.GeometryFlagsFor(materials[handle].Cutouts, materials[handle].Flags) != opaqueBefore)
        {
            // Structures bake each part's opaque flag in, so ones drawn in this material are rebuilt when next traced.
            // A background bake's held top level still points at the old ones, so it stops first.
            stopBakesThat(job => job.Area.Uses(handle));
            gpu.Tracing!.ForgetMaterial(handle);
        }
    }

    /// <summary>See <see cref="RendererAssets.DestroyMaterial"/>.</summary>
    public void Destroy(MaterialHandle material)
    {
        if (!gpu.Resources.Materials.IsLive(material))
        {
            throw new ArgumentException("The material does not exist.", nameof(material));
        }

        QueueDestroy(material.Index);
    }

    /// <summary>
    /// Destroys the materials of a model's parts and the textures they name, passing over parts that share a material
    /// or whose material is already gone.
    /// </summary>
    public void DestroyOfParts(ReadOnlySpan<DrawItem> parts)
    {
        foreach (ref readonly var part in parts)
        {
            if (!gpu.Resources.Materials.IsLive(part.Material))
            {
                continue;
            }

            foreach (int texture in gpu.Resources.Materials.TexturesOf(part.Material))
            {
                textures.DestroyIfLive(texture);
            }

            QueueDestroy(part.Material.Index);
        }
    }

    /// <summary>See <see cref="RendererAssets.CreateSurfaceShader"/>.</summary>
    public SurfaceShaderHandle CreateShader(byte[] spirv, string? name)
    {
        const uint spirvMagic = 0x07230203;
        if (spirv.Length < 20 || spirv.Length % sizeof(uint) != 0 || BitConverter.ToUInt32(spirv, 0) != spirvMagic)
        {
            throw new ArgumentException("This is not compiled SPIR-V code.", nameof(spirv));
        }

        return shaders.AddShader(spirv, name);
    }

    /// <summary>See <see cref="RendererAssets.ReloadShaders"/>.</summary>
    public int ReloadShaders(IReadOnlySet<string> names) => PipelineFactory.Reload(gpu.Context, names);

    private void QueueDestroy(int slot)
    {
        // The slot may go to a material with a different opaque flag, so no structure may keep using it, and a
        // background bake tracing those structures can't go on.
        var material = new MaterialHandle(slot);
        stopBakesThat(job => job.Area.Uses(material));
        gpu.Tracing?.ForgetMaterial(material);
        gpu.Resources.Materials.MarkDying(slot);
        gpu.Retired.AfterLastSubmitted.FreedMaterials.Add(slot);
    }
}
