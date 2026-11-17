using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The descriptor and pipeline layout calls every pass makes, without the structs round them. Counts come from the
/// spans, so none is written by hand.
/// </summary>
internal static unsafe class Descriptors
{
    /// <summary>Makes a set layout with these bindings.</summary>
    public static DescriptorSetLayout CreateSetLayout(this VulkanContext context, ReadOnlySpan<DescriptorSetLayoutBinding> bindings) =>
        context.CreateSetLayout(bindings, updateAfterBind: false);

    /// <summary>
    /// The same, made update-after-bind when asked, which only image bindings may be. Such a set counts against the
    /// limits for update-after-bind sets, which are far higher: MoltenVK allows 16 samplers a stage in ordinary sets, so
    /// a pass that samples more than that needs one. Its set must come from a pool made with
    /// <see cref="DescriptorPoolCreateFlags.UpdateAfterBindBit"/>.
    /// </summary>
    public static DescriptorSetLayout CreateSetLayout(
        this VulkanContext context, ReadOnlySpan<DescriptorSetLayoutBinding> bindings, bool updateAfterBind)
    {
        Span<DescriptorBindingFlags> flags = stackalloc DescriptorBindingFlags[bindings.Length];
        flags.Fill(DescriptorBindingFlags.UpdateAfterBindBit);
        fixed (DescriptorSetLayoutBinding* first = bindings)
        fixed (DescriptorBindingFlags* firstFlags = flags)
        {
            var flagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindingFlags = firstFlags,
            };
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                PNext = updateAfterBind ? &flagsInfo : null,
                Flags = updateAfterBind ? DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit : 0,
                BindingCount = (uint)bindings.Length,
                PBindings = first,
            };
            context.Api.CreateDescriptorSetLayout(context.Device, &info, null, out var layout).Check();
            return layout;
        }
    }

    /// <summary>Makes a pool with room for <paramref name="maxSets"/> sets holding these descriptors between them.</summary>
    public static DescriptorPool CreateDescriptorPool(this VulkanContext context, uint maxSets, params ReadOnlySpan<DescriptorPoolSize> sizes) =>
        context.CreateDescriptorPool(0, maxSets, sizes);

    /// <summary>The same, with flags, such as update-after-bind for a set whose layout is.</summary>
    public static DescriptorPool CreateDescriptorPool(
        this VulkanContext context, DescriptorPoolCreateFlags flags, uint maxSets, params ReadOnlySpan<DescriptorPoolSize> sizes)
    {
        fixed (DescriptorPoolSize* first = sizes)
        {
            var info = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = flags,
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

    /// <summary>A write of one image descriptor, such as a sampled texture or a storage image, into a set's binding.</summary>
    public static WriteDescriptorSet ImageWrite(DescriptorSet set, uint binding, DescriptorType type, DescriptorImageInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = set,
        DstBinding = binding,
        DescriptorCount = 1,
        DescriptorType = type,
        PImageInfo = info,
    };

    /// <summary>A write of one buffer descriptor into a set's binding, a storage buffer unless told otherwise.</summary>
    public static WriteDescriptorSet BufferWrite(
        DescriptorSet set, uint binding, DescriptorBufferInfo* info, DescriptorType type = DescriptorType.StorageBuffer) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = set,
        DstBinding = binding,
        DescriptorCount = 1,
        DescriptorType = type,
        PBufferInfo = info,
    };

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
