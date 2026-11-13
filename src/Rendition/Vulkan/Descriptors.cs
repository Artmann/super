using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The descriptor and pipeline layout calls every pass makes, without the structs round them. Counts come from the
/// spans, so none is written by hand.
/// </summary>
internal static unsafe class Descriptors
{
    /// <summary>Makes a set layout with these bindings.</summary>
    public static DescriptorSetLayout CreateSetLayout(this VulkanContext context, ReadOnlySpan<DescriptorSetLayoutBinding> bindings)
    {
        fixed (DescriptorSetLayoutBinding* first = bindings)
        {
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = first,
            };
            context.Api.CreateDescriptorSetLayout(context.Device, &info, null, out var layout).Check();
            return layout;
        }
    }

    /// <summary>Makes a pool with room for <paramref name="maxSets"/> sets holding these descriptors between them.</summary>
    public static DescriptorPool CreateDescriptorPool(this VulkanContext context, uint maxSets, params ReadOnlySpan<DescriptorPoolSize> sizes)
    {
        fixed (DescriptorPoolSize* first = sizes)
        {
            var info = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = maxSets,
                PoolSizeCount = (uint)sizes.Length,
                PPoolSizes = first,
            };
            context.Api.CreateDescriptorPool(context.Device, &info, null, out var pool).Check();
            return pool;
        }
    }

    /// <summary>Takes one set of a layout from a pool.</summary>
    public static DescriptorSet AllocateSet(this VulkanContext context, DescriptorPool pool, DescriptorSetLayout layout)
    {
        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        context.Api.AllocateDescriptorSets(context.Device, &info, out var set).Check();
        return set;
    }

    /// <summary>Fills <paramref name="sets"/> with sets of one layout from a pool.</summary>
    public static void AllocateSets(this VulkanContext context, DescriptorPool pool, DescriptorSetLayout layout, Span<DescriptorSet> sets)
    {
        Span<DescriptorSetLayout> layouts = stackalloc DescriptorSetLayout[sets.Length];
        layouts.Fill(layout);
        fixed (DescriptorSetLayout* firstLayout = layouts)
        fixed (DescriptorSet* firstSet = sets)
        {
            var info = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = (uint)sets.Length,
                PSetLayouts = firstLayout,
            };
            context.Api.AllocateDescriptorSets(context.Device, &info, firstSet).Check();
        }
    }

    /// <summary>Makes a pipeline layout over these set layouts, with a push constant range when one is given.</summary>
    public static PipelineLayout CreatePipelineLayout(
        this VulkanContext context, ReadOnlySpan<DescriptorSetLayout> setLayouts, PushConstantRange? pushRange = null)
    {
        var range = pushRange.GetValueOrDefault();
        fixed (DescriptorSetLayout* first = setLayouts)
        {
            var info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)setLayouts.Length,
                PSetLayouts = first,
                PushConstantRangeCount = pushRange is null ? 0u : 1u,
                PPushConstantRanges = pushRange is null ? null : &range,
            };
            context.Api.CreatePipelineLayout(context.Device, &info, null, out var layout).Check();
            return layout;
        }
    }
}
