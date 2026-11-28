using System.Numerics;
using Rendition.Gpu;

namespace Super.Tests.GpuLayer;

public class CommandListTests
{
    [Fact]
    public void Records_each_command_as_its_number_followed_by_its_arguments()
    {
        var commands = new CommandList();

        commands.SetPipeline(new PipelineId(7));
        commands.DrawIndexed(36, 1, 0, 0, 5);

        Assert.Equal([3, 7, 11, 36, 1, 0, 0, 5], commands.Words.ToArray());
    }

    [Fact]
    public void SetConstants_stores_bytes_padded_to_whole_words()
    {
        var commands = new CommandList();

        commands.SetConstants([1, 2, 3, 4, 5, 6]);

        int first = 1 | (2 << 8) | (3 << 16) | (4 << 24);
        int second = 5 | (6 << 8);
        Assert.Equal([9, 6, first, second], commands.Words.ToArray());
    }

    [Fact]
    public void SetConstants_with_no_bytes_records_only_the_count()
    {
        var commands = new CommandList();

        commands.SetConstants([]);

        Assert.Equal([9, 0], commands.Words.ToArray());
    }

    [Fact]
    public void A_render_pass_records_its_targets()
    {
        var commands = new CommandList();

        commands.BeginRenderPass(
            [new ColorTarget(new ViewId(4), LoadOp.Clear, new Vector4(0.5f, 0, 0, 1))],
            new DepthTarget(new ViewId(9), LoadOp.Load, 1, ReadOnly: true));
        commands.EndPass();

        Assert.Equal(
            [
                1, 1, 4, (int)LoadOp.Clear, Bits(0.5f), 0, 0, Bits(1),
                9, (int)LoadOp.Load, Bits(1), 1,
                2,
            ],
            commands.Words.ToArray());
    }

    [Fact]
    public void A_render_pass_without_depth_records_a_zero_view()
    {
        var commands = new CommandList();

        commands.BeginRenderPass([new ColorTarget(new ViewId(4), LoadOp.DontCare, default)], null);

        Assert.Equal([1, 1, 4, (int)LoadOp.DontCare, 0, 0, 0, 0, 0, 0, 0, 0], commands.Words.ToArray());
    }

    [Fact]
    public void Reset_empties_the_list_and_keeps_its_array()
    {
        var commands = new CommandList();
        Record(commands);
        commands.Reset();

        Assert.Equal(0, commands.Words.Length);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Record(commands);
        commands.Reset();
        Record(commands);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }

    [Fact]
    public void The_list_grows_past_its_first_array()
    {
        var commands = new CommandList();

        for (int i = 0; i < 1000; i++)
        {
            commands.Draw(3, 1, 0, i);
        }

        Assert.Equal(5000, commands.Words.Length);
        Assert.Equal([10, 3, 1, 0, 999], commands.Words[^5..].ToArray());
    }

    [Fact]
    public void A_label_records_the_index_of_its_name()
    {
        var commands = new CommandList();

        commands.PushLabel("Bloom");
        commands.PushLabel("Tonemap");
        commands.PopLabel();

        Assert.Equal([14, 0, 14, 1, 15], commands.Words.ToArray());
        Assert.Equal("Tonemap", commands.Label(1));
    }

    [Fact]
    public void EndPass_without_a_begin_throws()
    {
        var commands = new CommandList();

        var error = Assert.Throws<InvalidOperationException>(commands.EndPass);

        Assert.Equal("EndPass was called with no pass begun.", error.Message);
    }

    [Fact]
    public void Beginning_a_pass_inside_another_throws()
    {
        var commands = new CommandList();
        commands.BeginComputePass();

        var error = Assert.Throws<InvalidOperationException>(commands.BeginComputePass);

        Assert.Equal("BeginComputePass was called inside a pass that has not ended.", error.Message);
    }

    private static void Record(CommandList commands)
    {
        commands.PushLabel("Tonemap");
        commands.BeginRenderPass([new ColorTarget(new ViewId(2), LoadOp.Clear, Vector4.One)], null);
        commands.SetPipeline(new PipelineId(3));
        commands.SetBindGroup(0, new BindGroupId(4));
        commands.SetConstants([1, 2, 3, 4]);
        commands.Draw(3, 1, 0, 0);
        commands.EndPass();
        commands.PopLabel();
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
}
