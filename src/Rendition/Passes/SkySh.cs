using System.Numerics;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Rendition.Passes;

/// <summary>
/// Projects the sky cube the light probes read past the scene onto L1 spherical harmonics, per colour, into a 64-byte
/// buffer that chunks.glsl reads at set 0, binding 12: four vec4s, coefficient k's red, green and blue and its
/// luminance, unscaled by the environment's intensity. sky_sh.comp reads the cube's 16² mip, each texel weighted by its
/// solid angle, as a compute pass of the GPU layer. Recorded only on frames that draw a chunk's bake. See "Chunk
/// transfer" in docs/design.md.
/// </summary>
internal sealed unsafe class SkySh : IDisposable
{
    /// <summary>The buffer's size: four coefficients of four floats.</summary>
    public const int Size = 4 * 16;

    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly GpuBuffer _buffer;
    private readonly (BufferId Buffer, ViewId Sky) _adopted;
    private readonly ComputePass _pass;
    private readonly BindGroupId _group;
    private readonly CommandList _commands = new();

    /// <param name="sky">The sky cube, which keeps its view for the renderer's life and rests in the shader-read layout.</param>
    public SkySh((VulkanContext Context, VulkanGpu Gpu) device, ImageView sky)
    {
        var (context, gpu) = device;
        _context = context;
        _gpu = gpu;
        _buffer = GpuBuffer.CreateHostVisible(context, Size, BufferUsageFlags.StorageBufferBit);
        new Span<byte>(_buffer.Mapped, Size).Clear();

        _pass = new ComputePass(gpu, "sky_sh.comp");
        _adopted = (gpu.Adopt(_buffer.Handle, Size), gpu.Adopt(new AdoptedView(sky, default, default)));
        var sampler = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear));
        _group = gpu.CreateBindGroup(_pass.Pipeline, 0, [Bind.Image("sky", _adopted.Sky, sampler), Bind.Buffer("projected", _adopted.Buffer)]);
    }

    /// <summary>The projected sky, for the frame's set 0, binding 12.</summary>
    public Buffer Handle => _buffer.Handle;

    /// <summary>
    /// Projects the sky. The layer's barriers keep the write after the scene's earlier frames' reads, and the G-buffer
    /// pass's read after the write.
    /// </summary>
    public void Record(CommandBuffer cmd)
    {
        _commands.Reset();
        _pass.Record(_commands, [_group], [], (1, 1, 1));
        _gpu.Play(_commands, cmd);
    }

    /// <summary>Projects the sky now and waits, then reads the coefficients back. Stalls the GPU, so for tests.</summary>
    public Vector4[] Project()
    {
        _context.SubmitAndWait(cmd =>
        {
            Record(cmd);
            _context.Api.MemoryBarrier(cmd,
                (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit), (PipelineStageFlags2.HostBit, AccessFlags2.HostReadBit));
        });
        return new ReadOnlySpan<Vector4>(_buffer.Mapped, 4).ToArray();
    }

    public void Dispose()
    {
        _gpu.Destroy(_group);
        _gpu.Release(_adopted.Sky);
        _gpu.Release(_adopted.Buffer);
        _pass.Dispose();
        _buffer.Dispose();
    }
}
