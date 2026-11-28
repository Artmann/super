using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Tests;

// Meshes drawn through the GPU layer with test_mesh.vert, which places each corner at its position plus its instance's
// offset and colours it by its instance. Clip space is Vulkan's, with y down, so a pixel's row grows with y.
[Collection(GpuCollection.Name)]
[Trait("Needs", "GPU")]
public sealed class MeshDrawGpuTests(GpuFixture gpu)
{
    private const int Size = 64;

    private static readonly byte[] Black = [0, 0, 0, 255];
    private static readonly byte[] Red = [255, 0, 0, 255];
    private static readonly byte[] Green = [0, 255, 0, 255];
    private static readonly byte[] Blue = [0, 0, 255, 255];

    // A triangle bigger than the screen, so it covers every pixel. Top left, bottom left, top right: counter-clockwise
    // on the screen, so it faces the camera.
    private static readonly Vector3[] FacingCover = [new(-1f, -1f, 0.5f), new(-1f, 3f, 0.5f), new(3f, -1f, 0.5f)];

    // The same corners the other way round, so it faces away.
    private static readonly Vector3[] AwayCover = [FacingCover[0], FacingCover[2], FacingCover[1]];

    [Fact]
    public void The_nearer_of_two_triangles_wins_the_depth_test()
    {
        using var bench = new Bench(gpu, depth: true);
        var pipeline = bench.Pipeline(depth: new DepthState(GpuFormat.Depth32Float, CompareFunction.Greater, Write: true));

        // Reversed-Z: the far cover at depth 0.25, the near one at 0.75. The near one is drawn first.
        Vector3[] corners = [.. FacingCover.Select(c => c with { Z = 0.25f }), .. FacingCover.Select(c => c with { Z = 0.75f })];
        var vertices = bench.Buffer<Vector3>(corners, BufferUsageFlags.VertexBufferBit);
        var instances = bench.RedThenGreen();

        var pixels = bench.Draw(commands =>
        {
            commands.SetPipeline(pipeline);
            commands.SetVertexBuffer(0, vertices, 0);
            commands.SetVertexBuffer(1, instances, 0);
            commands.Draw(3, 1, 3, 1);
            commands.Draw(3, 1, 0, 0);
        });

        Assert.Equal(Green, Pixel(pixels, 32, 32));
        bench.AssertNoValidationErrors();
    }

    [Fact]
    public void A_back_facing_triangle_is_culled_unless_culling_is_off()
    {
        using var bench = new Bench(gpu, depth: false);
        var instances = bench.Buffer<Instance>([new(Vector2.Zero, Red)], BufferUsageFlags.VertexBufferBit);
        var facing = bench.Buffer<Vector3>(FacingCover, BufferUsageFlags.VertexBufferBit);
        var away = bench.Buffer<Vector3>(AwayCover, BufferUsageFlags.VertexBufferBit);

        byte[] Draw(CullMode cull, BufferId vertices)
        {
            var pipeline = bench.Pipeline(cull: cull);
            return Pixel(bench.Draw(commands =>
            {
                commands.SetPipeline(pipeline);
                commands.SetVertexBuffer(0, vertices, 0);
                commands.SetVertexBuffer(1, instances, 0);
                commands.Draw(3, 1, 0, 0);
            }), 32, 32);
        }

        Assert.Equal(Red, Draw(CullMode.Back, facing));
        Assert.Equal(Black, Draw(CullMode.Back, away));
        Assert.Equal(Black, Draw(CullMode.Front, facing));
        Assert.Equal(Red, Draw(CullMode.None, away));
        bench.AssertNoValidationErrors();
    }

    [Fact]
    public void Each_instance_reads_its_own_attributes()
    {
        using var bench = new Bench(gpu, depth: false);
        var pipeline = bench.Pipeline();

        // A small right-angled triangle with its corner at (-0.25, -0.25), drawn left of the middle in red and right of
        // it in blue.
        var vertices = bench.Buffer<Vector3>(
            [new(-0.25f, -0.25f, 0.5f), new(-0.25f, 0.25f, 0.5f), new(0.25f, -0.25f, 0.5f)], BufferUsageFlags.VertexBufferBit);
        var instances = bench.Buffer<Instance>(
            [new(new Vector2(-0.5f, 0f), Red), new(new Vector2(0.5f, 0f), Blue)], BufferUsageFlags.VertexBufferBit);

        var pixels = bench.Draw(commands =>
        {
            commands.SetPipeline(pipeline);
            commands.SetVertexBuffer(0, vertices, 0);
            commands.SetVertexBuffer(1, instances, 0);
            commands.Draw(3, 2, 0, 0);
        });

        // Clip (-0.6, -0.1) is pixel (12.8, 28.8), inside the left triangle; (0.4, -0.1) is (44.8, 28.8), inside the right.
        Assert.Equal(Red, Pixel(pixels, 13, 29));
        Assert.Equal(Blue, Pixel(pixels, 45, 29));
        Assert.Equal(Black, Pixel(pixels, 32, 32));
        bench.AssertNoValidationErrors();
    }

    [Fact]
    public void An_indexed_draw_uses_the_index_buffer_and_first_instance()
    {
        using var bench = new Bench(gpu, depth: false);
        var pipeline = bench.Pipeline();

        // Four corners off the screen, then the square from -0.5 to 0.5, which the base vertex of 4 starts at.
        Vector3 off = new(5f, 5f, 0.5f);
        var vertices = bench.Buffer<Vector3>(
            [off, off, off, off, new(-0.5f, -0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, 0.5f)],
            BufferUsageFlags.VertexBufferBit);
        var indices = bench.Buffer<uint>([0, 1, 2, 2, 1, 3], BufferUsageFlags.IndexBufferBit);
        var instances = bench.RedThenGreen();

        var pixels = bench.Draw(commands =>
        {
            commands.SetPipeline(pipeline);
            commands.SetVertexBuffer(0, vertices, 0);
            commands.SetVertexBuffer(1, instances, 0);
            commands.SetIndexBuffer(indices, 0);
            commands.DrawIndexed(6, 1, 0, 4, 1);
        });

        // The square covers pixels 16 to 48 each way.
        Assert.Equal(Green, Pixel(pixels, 20, 20));
        Assert.Equal(Green, Pixel(pixels, 44, 44));
        Assert.Equal(Black, Pixel(pixels, 8, 8));
        Assert.Equal(Black, Pixel(pixels, 56, 56));
        bench.AssertNoValidationErrors();
    }

    [Fact]
    public void A_scissor_keeps_the_draw_inside_its_rectangle()
    {
        using var bench = new Bench(gpu, depth: false);
        var pixels = bench.DrawCover(commands => commands.SetScissor(0, 0, 32, 64));

        Assert.Equal(Red, Pixel(pixels, 16, 32));
        Assert.Equal(Black, Pixel(pixels, 48, 32));
        bench.AssertNoValidationErrors();
    }

    [Fact]
    public void A_viewport_maps_the_draw_into_its_rectangle()
    {
        using var bench = new Bench(gpu, depth: false);

        // The top right quarter.
        var pixels = bench.DrawCover(commands => commands.SetViewport(32f, 0f, 32f, 32f));

        Assert.Equal(Red, Pixel(pixels, 48, 16));
        Assert.Equal(Black, Pixel(pixels, 16, 16));
        Assert.Equal(Black, Pixel(pixels, 48, 48));
        bench.AssertNoValidationErrors();
    }

    [Fact]
    public void An_override_constant_changes_what_the_shader_does()
    {
        using var bench = new Bench(gpu, depth: false);

        Assert.Equal(Red, Pixel(bench.DrawCover(_ => { }), 32, 32));
        Assert.Equal(Blue, Pixel(bench.DrawCover(_ => { }, overrides: [1]), 32, 32));
        bench.AssertNoValidationErrors();
    }

    [Fact]
    public void A_target_whose_writes_are_off_keeps_what_it_held()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var first = LayerImages.Color(gpu.Context, Size);
        using var second = LayerImages.Color(gpu.Context, Size);
        byte[] pattern = LayerImages.Pattern(Size);
        LayerImages.Upload(gpu.Context, second, Size, pattern);
        var pipeline = device.CreateRenderPipeline(new RenderPipelineDescription
        {
            VertexShader = device.CreateShader("fullscreen.vert"),
            FragmentShader = device.CreateShader("solid_colors.frag"),
            Targets = [new ColorTargetState(GpuFormat.Rgba8Unorm), new ColorTargetState(GpuFormat.Rgba8Unorm, Writes: ColorWrites.None)],
        });
        ReadOnlySpan<Vector4> colors = [new(1f, 0f, 0f, 1f), new(0f, 0f, 1f, 1f)];
        var commands = new CommandList();
        commands.BeginRenderPass(
            [new ColorTarget(device.Adopt(first.View, first.Image, new Extent2D(Size, Size)), LoadOp.DontCare, default),
                new ColorTarget(device.Adopt(second.View, second.Image, new Extent2D(Size, Size)), LoadOp.Load, default)],
            null);
        commands.SetPipeline(pipeline);
        commands.SetConstants(MemoryMarshal.AsBytes(colors));
        commands.Draw(3, 1, 0, 0);
        commands.EndPass();

        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));

        Assert.Equal(Red, Pixel(LayerImages.Read(gpu.Context, first, Size), 32, 32));
        Assert.Equal(pattern, LayerImages.Read(gpu.Context, second, Size));
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Recording_and_playing_an_indexed_instanced_draw_again_allocates_nothing()
    {
        using var bench = new Bench(gpu, depth: true);
        var pipeline = bench.Pipeline(depth: new DepthState(GpuFormat.Depth32Float, CompareFunction.Greater, Write: true), cull: CullMode.Back);
        var vertices = bench.Buffer<Vector3>(FacingCover, BufferUsageFlags.VertexBufferBit);
        var indices = bench.Buffer<uint>([0, 1, 2], BufferUsageFlags.IndexBufferBit);
        var instances = bench.RedThenGreen();
        long allocated = LayerImages.AllocatedPlaying(gpu.Context, bench.Device, commands =>
        {
            bench.Begin(commands);
            commands.SetPipeline(pipeline);
            commands.SetViewport(0f, 0f, Size, Size);
            commands.SetScissor(0, 0, Size, Size);
            commands.SetVertexBuffer(0, vertices, 0);
            commands.SetVertexBuffer(1, instances, 0);
            commands.SetIndexBuffer(indices, 0);
            commands.DrawIndexed(3, 2, 0, 0, 0);
            commands.EndPass();
        });

        Assert.Equal(0, allocated);
        bench.AssertNoValidationErrors();
    }

    private static byte[] Pixel(byte[] pixels, int x, int y) => pixels.AsSpan((y * Size + x) * 4, 4).ToArray();

    // The colour packed as test_mesh.vert reads it: Unorm8x4, red in the lowest byte.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Instance(Vector2 offset, byte[] color)
    {
        public readonly Vector2 Offset = offset;
        public readonly uint Color = (uint)(color[0] | color[1] << 8 | color[2] << 16 | color[3] << 24);
    }

    // A device, a colour target cleared to black, a depth target when asked for, and the buffers a test makes.
    private sealed class Bench : IDisposable
    {
        private readonly GpuFixture _gpu;
        private readonly int _errors;
        private readonly RenderTarget _target;
        private readonly RenderTarget? _depth;
        private readonly ViewId _view;
        private readonly ViewId _depthView;
        private readonly List<GpuBuffer> _buffers = [];

        public Bench(GpuFixture gpu, bool depth)
        {
            _gpu = gpu;
            _errors = gpu.ValidationErrors;
            Device = new VulkanGpu(gpu.Context);
            _target = LayerImages.Color(gpu.Context, Size);
            _view = Device.Adopt(_target.View, _target.Image, new Extent2D(Size, Size));
            if (depth)
            {
                _depth = LayerImages.Depth(gpu.Context, Size);
                _depthView = Device.Adopt(_depth.View, _depth.Image, new Extent2D(Size, Size));
            }
        }

        public VulkanGpu Device { get; }

        public PipelineId Pipeline(DepthState? depth = null, CullMode cull = CullMode.None, ReadOnlySpan<uint> overrides = default) =>
            Device.CreateRenderPipeline(new RenderPipelineDescription
            {
                VertexShader = Device.CreateShader("test_mesh.vert"),
                FragmentShader = Device.CreateShader("test_mesh.frag"),
                Targets = [new ColorTargetState(GpuFormat.Rgba8Unorm)],
                Vertices =
                [
                    new VertexLayout(12, PerInstance: false, [new VertexAttribute(0, VertexFormat.Float32x3, 0)]),
                    new VertexLayout(12, PerInstance: true,
                        [new VertexAttribute(1, VertexFormat.Float32x2, 0), new VertexAttribute(2, VertexFormat.Unorm8x4, 8)]),
                ],
                Depth = depth,
                Cull = cull,
                Overrides = overrides,
            });

        /// <summary>Two instances at the centre, red then green, for the second vertex slot.</summary>
        public BufferId RedThenGreen() => Buffer<Instance>([new(Vector2.Zero, Red), new(Vector2.Zero, Green)], BufferUsageFlags.VertexBufferBit);

        public unsafe BufferId Buffer<T>(ReadOnlySpan<T> items, BufferUsageFlags usage) where T : unmanaged
        {
            var bytes = MemoryMarshal.AsBytes(items);
            var buffer = GpuBuffer.CreateHostVisible(_gpu.Context, (ulong)bytes.Length, usage);
            bytes.CopyTo(new Span<byte>(buffer.Mapped, bytes.Length));
            _buffers.Add(buffer);
            return Device.Adopt(buffer.Handle, bytes.Length);
        }

        /// <summary>Begins a pass that clears the colour target to black and the depth target, if any, to the far plane.</summary>
        public void Begin(CommandList commands) => commands.BeginRenderPass(
            [new ColorTarget(_view, LoadOp.Clear, new Vector4(0f, 0f, 0f, 1f))],
            _depth is null ? null : new DepthTarget(_depthView, LoadOp.Clear, 0f, ReadOnly: false));

        /// <summary>Plays a pass with what <paramref name="body"/> records and returns the colour target's pixels.</summary>
        public byte[] Draw(Action<CommandList> body)
        {
            var commands = new CommandList();
            Begin(commands);
            body(commands);
            commands.EndPass();
            _gpu.Context.SubmitAndWait(cmd => Device.Play(commands, cmd));
            return LayerImages.Read(_gpu.Context, _target, Size);
        }

        /// <summary>Draws a red cover over the screen with <paramref name="before"/> recorded first.</summary>
        public byte[] DrawCover(Action<CommandList> before, ReadOnlySpan<uint> overrides = default)
        {
            var pipeline = Pipeline(overrides: overrides);
            var vertices = Buffer<Vector3>(FacingCover, BufferUsageFlags.VertexBufferBit);
            var instances = Buffer<Instance>([new(Vector2.Zero, Red)], BufferUsageFlags.VertexBufferBit);
            return Draw(commands =>
            {
                commands.SetPipeline(pipeline);
                before(commands);
                commands.SetVertexBuffer(0, vertices, 0);
                commands.SetVertexBuffer(1, instances, 0);
                commands.Draw(3, 1, 0, 0);
            });
        }

        public void AssertNoValidationErrors() => Assert.Equal(_errors, _gpu.ValidationErrors);

        public void Dispose()
        {
            Device.Dispose();
            foreach (var buffer in _buffers)
            {
                buffer.Dispose();
            }

            _depth?.Dispose();
            _target.Dispose();
        }
    }
}
