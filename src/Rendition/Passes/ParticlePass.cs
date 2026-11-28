using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Blends soft puffs over the lit image, in turn with glass. Each particle is a square
/// facing the camera, drawn from one instance of a vertex buffer, back to front. Depth
/// isn't tested: the fragment shader reads the depth buffer itself, so a puff thins out
/// as it nears a surface instead of being cut off by it. <see cref="TransparentPass"/>
/// begins the rendering and draws runs of particles between panes of glass.
/// </summary>
internal sealed unsafe class ParticlePass : IDisposable
{
    public const int MaxParticles = Particle.MaxPerFrame;

    /// <summary>Depth, the shadow map with comparison, the sky's light, the sky itself and the background without the sun.</summary>
    private const int ImageCount = 5;

    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _layout;

    // The pipeline, the set and each frame's buffer, adopted into the GPU layer; see Bind.
    private readonly PipelineId _pipeline;
    private readonly BindGroupId _group;
    private readonly GpuBuffer[] _buffers;
    private readonly BufferId[] _vertices;
    private readonly ParticleOrder _order = new();

    /// <summary>Must match the Camera block in particle.vert and particle.frag.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CameraConstants
    {
        public Vector4 Right;
        public Vector4 Up;
        public Vector4 Forward;
    }

    public ParticlePass((VulkanContext Context, VulkanGpu Gpu) device, DescriptorSetLayout frameSetLayout, int framesInFlight)
    {
        var context = device.Context;
        _context = context;
        _gpu = device.Gpu;
        _buffers = new GpuBuffer[framesInFlight];
        _vertices = new BufferId[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            _buffers[i] = GpuBuffer.CreateHostVisible(
                context, (ulong)(MaxParticles * sizeof(Particle)), BufferUsageFlags.VertexBufferBit);
            _vertices[i] = _gpu.Adopt(_buffers[i].Handle, (long)_buffers[i].Size);
        }

        const ShaderStageFlags stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
        Span<DescriptorSetLayoutBinding> bindings = stackalloc DescriptorSetLayoutBinding[ImageCount];
        for (int i = 0; i < ImageCount; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding((uint)i, DescriptorType.CombinedImageSampler, 1, stages);
        }

        _setLayout = context.CreateSetLayout(bindings);
        _pool = context.CreateDescriptorPool(1, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, ImageCount));
        _set = context.AllocateSet(_pool, _setLayout);
        _layout = context.CreatePipelineLayout(
            [frameSetLayout, _setLayout], new PushConstantRange(stages, 0, (uint)sizeof(CameraConstants)));
        _pipeline = _gpu.Adopt(CreatePipeline(context, _layout), _layout, [frameSetLayout, _setLayout], stages);
        _group = _gpu.Adopt(_set);
    }

    // Each particle is one instance of the vertex buffer, blended over the lit picture without a depth test.
    private static LivePipeline CreatePipeline(VulkanContext context, PipelineLayout layout)
    {
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
        return PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "particle.vert",
            FragmentShader = "particle.frag",
            Layout = layout,
            ColorFormats = [GBuffer.HdrFormat],
            DepthFormat = GBuffer.DepthFormat,
            IgnoreDepth = true,
            VertexBindings = vertexBindings,
            VertexAttributes = attributes,
            Transparent = true,
        });
    }

    /// <summary>Points the pass at the current depth buffer, shadow map and sky. Call again after a resize.</summary>
    public void WriteTargets(
        DescriptorImageInfo depth, DescriptorImageInfo shadowCompare, DescriptorImageInfo irradiance, DescriptorImageInfo reflections,
        DescriptorImageInfo background)
    {
        var images = stackalloc DescriptorImageInfo[] { depth, shadowCompare, irradiance, reflections, background };
        var writes = stackalloc WriteDescriptorSet[ImageCount];
        for (int i = 0; i < ImageCount; i++)
        {
            writes[i] = Descriptors.ImageWrite(_set, (uint)i, DescriptorType.CombinedImageSampler, &images[i]);
        }

        _context.Api.UpdateDescriptorSets(_context.Device, ImageCount, writes, 0, null);
    }

    /// <summary>This frame's particles as uploaded, furthest first.</summary>
    public ReadOnlySpan<Particle> Sorted => _order.Sorted;

    /// <summary>Copies this frame's particles to the GPU, furthest first, and returns how many fit.</summary>
    public int Upload(int frameIndex, ReadOnlySpan<Particle> particles, Vector3 camera, Vector3 forward)
    {
        var sorted = _order.Sort(particles, camera, forward);
        sorted.CopyTo(new Span<Particle>(_buffers[frameIndex].Mapped, MaxParticles));
        return sorted.Length;
    }

    /// <summary>
    /// Readies drawing particles inside a render pass another list began, with the frame's set as group 0. It sets
    /// every group the particles read, since what was drawn before may have used another layout.
    /// </summary>
    public void Bind(CommandList commands, BindGroupId frame, int frameIndex, in CameraConstants camera)
    {
        commands.SetPipeline(_pipeline);
        commands.SetBindGroup(0, frame);
        commands.SetBindGroup(1, _group);
        commands.SetConstants(MemoryMarshal.AsBytes(new ReadOnlySpan<CameraConstants>(in camera)));
        commands.SetVertexBuffer(0, _vertices[frameIndex], 0);
    }

    /// <summary>Draws <paramref name="count"/> particles of the sorted list from <paramref name="first"/>, after <see cref="Bind"/>.</summary>
    public static void Draw(CommandList commands, int first, int count) => commands.Draw(6, count, 0, first);

    public void Dispose()
    {
        for (int i = 0; i < _buffers.Length; i++)
        {
            _gpu.Release(_vertices[i]);
            _buffers[i].Dispose();
        }

        var api = _context.Api;
        _gpu.Destroy(_pipeline);
        _gpu.Destroy(_group);
        api.DestroyPipelineLayout(_context.Device, _layout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }
}
