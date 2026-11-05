using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Blends soft puffs over the lit image, before glass. Each particle is a square facing
/// the camera, drawn from one instance of a vertex buffer, back to front. There's no
/// depth attachment: the fragment shader reads the depth buffer itself, so a puff thins
/// out as it nears a surface instead of being cut off by it.
/// </summary>
internal sealed unsafe class ParticlePass : IDisposable
{
    public const int MaxParticles = Particle.MaxPerFrame;

    private readonly VulkanContext _context;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _layout;
    private readonly Pipeline _pipeline;
    private readonly GpuBuffer[] _buffers;
    private readonly float[] _keys = new float[MaxParticles];
    private readonly int[] _order = new int[MaxParticles];

    /// <summary>Must match the Camera block in particle.vert and particle.frag.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CameraConstants
    {
        public Vector4 Right;
        public Vector4 Up;
        public Vector4 Forward;
    }

    public ParticlePass(VulkanContext context, DescriptorSetLayout frameSetLayout, int framesInFlight)
    {
        _context = context;
        var api = context.Api;
        _buffers = new GpuBuffer[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            _buffers[i] = GpuBuffer.CreateHostVisible(
                context, (ulong)(MaxParticles * sizeof(Particle)), BufferUsageFlags.VertexBufferBit);
        }

        // Depth, the shadow map with comparison, the sky's light and the sky itself.
        const ShaderStageFlags stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.CombinedImageSampler, 1, stages),
            new(1, DescriptorType.CombinedImageSampler, 1, stages),
            new(2, DescriptorType.CombinedImageSampler, 1, stages),
            new(3, DescriptorType.CombinedImageSampler, 1, stages),
        };
        var setLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 4,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &setLayoutInfo, null, out _setLayout).Check();

        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 4);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        var setLayout = _setLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        api.AllocateDescriptorSets(context.Device, &allocInfo, out _set).Check();

        var setLayouts = stackalloc DescriptorSetLayout[] { frameSetLayout, _setLayout };
        var pushRange = new PushConstantRange(stages, 0, (uint)sizeof(CameraConstants));
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        api.CreatePipelineLayout(context.Device, &layoutInfo, null, out _layout).Check();

        ReadOnlySpan<VertexInputBindingDescription> vertexBindings =
        [
            new(0, (uint)sizeof(Particle), VertexInputRate.Instance),
        ];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            // Position and size sit together, so they arrive as one vec4.
            new(0, 0, Format.R32G32B32A32Sfloat, (uint)Marshal.OffsetOf<Particle>(nameof(Particle.Position))),
            new(1, 0, Format.R32G32B32A32Sfloat, (uint)Marshal.OffsetOf<Particle>(nameof(Particle.Color))),
            new(2, 0, Format.R32G32B32Sfloat, (uint)Marshal.OffsetOf<Particle>(nameof(Particle.Rotation))),
        ];
        _pipeline = PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "particle.vert",
            FragmentShader = "particle.frag",
            Layout = _layout,
            ColorFormats = [GBuffer.HdrFormat],
            DepthFormat = Format.Undefined,
            VertexBindings = vertexBindings,
            VertexAttributes = attributes,
            Transparent = true,
        });
    }

    /// <summary>Points the pass at the current depth buffer, shadow map and sky. Call again after a resize.</summary>
    public void WriteTargets(
        DescriptorImageInfo depth, DescriptorImageInfo shadowCompare, DescriptorImageInfo irradiance, DescriptorImageInfo reflections)
    {
        var images = stackalloc DescriptorImageInfo[] { depth, shadowCompare, irradiance, reflections };
        var writes = stackalloc WriteDescriptorSet[4];
        for (int i = 0; i < 4; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &images[i],
            };
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 4, writes, 0, null);
    }

    /// <summary>Copies this frame's particles to the GPU, furthest first, and returns how many fit.</summary>
    public int Upload(int frameIndex, ReadOnlySpan<Particle> particles, Vector3 camera, Vector3 forward)
    {
        var fitting = particles[..Math.Min(particles.Length, MaxParticles)];
        var target = new Span<Particle>(_buffers[frameIndex].Mapped, MaxParticles);
        SortBackToFront(fitting, camera, forward, _keys, _order, target);
        return fitting.Length;
    }

    /// <summary>
    /// Writes particles into a buffer furthest from the camera first, so each blends over
    /// the ones behind it. Distance is measured along the view, which suits both kinds of
    /// camera. Keys and order are scratch space at least as long as the particles.
    /// </summary>
    internal static void SortBackToFront(
        ReadOnlySpan<Particle> particles, Vector3 camera, Vector3 forward, Span<float> keys, Span<int> order, Span<Particle> into)
    {
        int count = particles.Length;
        var sortKeys = keys[..count];
        var sortOrder = order[..count];
        for (int i = 0; i < count; i++)
        {
            // Negated, so the furthest has the smallest key and comes first.
            sortKeys[i] = -Vector3.Dot(particles[i].Position - camera, forward);
            sortOrder[i] = i;
        }

        sortKeys.Sort(sortOrder);
        for (int i = 0; i < count; i++)
        {
            into[i] = particles[sortOrder[i]];
        }
    }

    public void Record(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, int frameIndex, int count, in CameraConstants camera)
    {
        if (count == 0)
        {
            return;
        }

        var api = _context.Api;
        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = targets.Hdr.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, targets.Extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
        };

        api.CmdBeginRendering(cmd, &rendering);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        api.SetFullViewport(cmd, targets.Extent);

        var sets = stackalloc DescriptorSet[] { frameSet, _set };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 2, sets, 0, null);
        var constants = camera;
        api.CmdPushConstants(
            cmd, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(CameraConstants), &constants);

        var buffer = _buffers[frameIndex].Handle;
        ulong offset = 0;
        api.CmdBindVertexBuffers(cmd, 0, 1, &buffer, &offset);
        api.CmdDraw(cmd, 6, (uint)count, 0, 0);
        api.CmdEndRendering(cmd);
    }

    public void Dispose()
    {
        foreach (var buffer in _buffers)
        {
            buffer.Dispose();
        }

        var api = _context.Api;
        api.DestroyPipeline(_context.Device, _pipeline, null);
        api.DestroyPipelineLayout(_context.Device, _layout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }
}
