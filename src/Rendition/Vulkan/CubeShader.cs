using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;

namespace Rendition.Vulkan;

/// <summary>
/// A compute shader, run through the GPU layer, that writes one level of an <see cref="EnvironmentCube"/>, eight by
/// eight texels a group and one face per z, with <typeparamref name="TConstants"/> as its push constants. Its callers
/// make its bind groups for <see cref="Pipeline"/>, binding the level it writes as <c>target</c>.
/// </summary>
internal sealed class CubeShader<TConstants> : IDisposable where TConstants : unmanaged
{
    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly ComputePass _pass;
    private readonly CommandList _commands = new();

    public CubeShader((VulkanContext Context, VulkanGpu Gpu) device, string shader)
    {
        (_context, _gpu) = device;
        _pass = new ComputePass(_gpu, shader);
    }

    /// <summary>The pipeline the shader's bind groups are made for.</summary>
    public PipelineId Pipeline => _pass.Pipeline;

    /// <summary>
    /// Writes the whole of one level of <paramref name="target"/> with <paramref name="group"/> set, leaving it in
    /// General, as the group's storage view of it expects.
    /// </summary>
    public void Record(CommandBuffer cmd, BindGroupId group, CubeLevel target, TConstants constants)
    {
        // Moving a single mip of a cube is beyond the layer until it tracks layouts, so it is moved here.
        _context.Api.Transition(cmd,
            new ImageTransition(target.Image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.General) { BaseMip = (uint)target.Mip, LayerCount = 6 },
            (PipelineStageFlags2.AllCommandsBit, AccessFlags2.None),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit));

        int groups = (int)(target.Size + 7) / 8;
        _commands.Reset();
        _pass.Record(_commands, [group], MemoryMarshal.AsBytes(new ReadOnlySpan<TConstants>(in constants)), (groups, groups, 6));
        _gpu.Play(_commands, cmd);
    }

    public void Dispose() => _pass.Dispose();
}
