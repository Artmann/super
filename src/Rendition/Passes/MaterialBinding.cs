using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// The bind group a scene draw's material reads through, group 1 of the scene's pipelines. On Vulkan every material is
/// in one bindless table; a backend without bindless arrays gives each material a group of its own.
/// </summary>
internal abstract class MaterialBinding
{
    public abstract BindGroupId For(MaterialHandle material);
}

/// <summary>Every material through the one bindless table, as the layer adopted it.</summary>
internal sealed class GlobalMaterialBinding(VulkanGpu gpu, ResourceTable resources) : MaterialBinding
{
    private readonly BindGroupId _table = gpu.Adopt(resources.Set);

    public override BindGroupId For(MaterialHandle material) => _table;
}
