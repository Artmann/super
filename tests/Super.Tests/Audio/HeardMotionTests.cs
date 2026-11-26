using System.Numerics;

namespace Super.Tests.Audio;

public class HeardMotionTests
{
    [Fact]
    public void TheFirstPlaceSeenIsStill()
    {
        var motion = new HeardMotion();

        Assert.Equal(Vector3.Zero, motion.Update(new Vector3(5, 0, 0), 3.0));
    }

    [Fact]
    public void ASteadyMoveSettlesOnItsSpeed()
    {
        var motion = new HeardMotion();
        Vector3 velocity = Vector3.Zero;

        // 10 m/s along X for a second at 60 frames a second.
        for (int frame = 0; frame <= 60; frame++)
        {
            velocity = motion.Update(new Vector3(frame * 10f / 60f, 0, 0), frame / 60.0);
        }

        Assert.Equal(10f, velocity.X, 0.01f);
        Assert.Equal(0f, velocity.Y);
        Assert.Equal(0f, velocity.Z);
    }

    [Fact]
    public void TheSpeedIsSmoothedRatherThanJumping()
    {
        var motion = new HeardMotion();
        motion.Update(Vector3.Zero, 0);

        // One frame at 10 m/s goes a sixth of the way: 1 - e^(-(1/60) / 0.1) = 0.1535.
        Vector3 velocity = motion.Update(new Vector3(10f / 60f, 0, 0), 1 / 60.0);

        Assert.Equal(1.535f, velocity.X, 0.001f);
    }

    [Fact]
    public void AJumpStartsAgainFromStill()
    {
        var motion = new HeardMotion();
        for (int frame = 0; frame <= 60; frame++)
        {
            motion.Update(new Vector3(frame * 10f / 60f, 0, 0), frame / 60.0);
        }

        // 30 m in a sixtieth of a second is 1800 m/s: a teleport, not a move.
        Vector3 velocity = motion.Update(new Vector3(40, 0, 0), 61 / 60.0);

        Assert.Equal(Vector3.Zero, velocity);
    }

    [Fact]
    public void AMoveWithNoTimePassingIsAJump()
    {
        var motion = new HeardMotion();
        motion.Update(Vector3.Zero, 0);
        motion.Update(new Vector3(0.1f, 0, 0), 1 / 60.0);

        Assert.Equal(Vector3.Zero, motion.Update(new Vector3(0.2f, 0, 0), 1 / 60.0));
    }

    [Fact]
    public void StoppingSettlesOnExactlyStill()
    {
        var motion = new HeardMotion();
        for (int frame = 0; frame <= 60; frame++)
        {
            motion.Update(new Vector3(frame * 10f / 60f, 0, 0), frame / 60.0);
        }

        Vector3 velocity = Vector3.One;
        for (int frame = 61; frame <= 180; frame++)
        {
            velocity = motion.Update(new Vector3(10, 0, 0), frame / 60.0);
        }

        Assert.Equal(Vector3.Zero, velocity);
    }
}
