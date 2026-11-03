namespace Super.Tests.Audio;

public class AudioCommandsTests
{
    private static AudioCommand Volume(float value) => new() { Kind = AudioCommandKind.ChannelVolume, Value = value };

    private static List<float> Drain(AudioCommands ring)
    {
        var values = new List<float>();
        while (ring.TryPop(out AudioCommand command))
        {
            values.Add(command.Value);
        }

        return values;
    }

    [Fact]
    public void FullRingDropsWithoutBlocking()
    {
        var ring = new AudioCommands();
        for (int i = 0; i < 5000; i++)
        {
            ring.Push(Volume(i));
        }

        ring.Publish();

        List<float> drained = Drain(ring);
        Assert.Equal(4096, drained.Count);
        Assert.Equal(904, ring.Dropped);

        // The ones that fit are the first ones, in order.
        Assert.Equal(0f, drained[0]);
        Assert.Equal(4095f, drained[^1]);
    }

    [Fact]
    public void CommandsStayHiddenUntilPublished()
    {
        var ring = new AudioCommands(4);
        ring.Push(Volume(1));

        Assert.Empty(Drain(ring));
        ring.Publish();
        Assert.Equal([1f], Drain(ring));
    }

    [Fact]
    public void DrainedSpaceIsUsedAgainInOrder()
    {
        var ring = new AudioCommands(4);
        var drained = new List<float>();
        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < 3; i++)
            {
                Assert.True(ring.Push(Volume(round * 3 + i)));
            }

            ring.Publish();
            drained.AddRange(Drain(ring));
        }

        Assert.Equal(Enumerable.Range(0, 15).Select(i => (float)i), drained);
        Assert.Equal(0, ring.Dropped);
    }
}
