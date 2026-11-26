using System.Numerics;

namespace Super.Tests.Audio;

public class SpatialTests
{
    [Fact]
    public void FalloffAtHandWorkedPoints()
    {
        Assert.Equal(1f, Spatial.Falloff(1, 2, 50));
        Assert.Equal(1f, Spatial.Falloff(2, 2, 50));
        Assert.Equal(0.4792f, Spatial.Falloff(4, 2, 50), 1e-4f);
        Assert.Equal(0f, Spatial.Falloff(50, 2, 50));
        Assert.Equal(0f, Spatial.Falloff(80, 2, 50));
    }

    [Fact]
    public void SourceToTheRightPansRight()
    {
        Vector3 forward = -Vector3.UnitZ;

        Assert.Equal(1f, Spatial.Pan(Vector3.Zero, forward, new Vector3(5, 0, 0)), 1e-6f);
        Assert.Equal(-1f, Spatial.Pan(Vector3.Zero, forward, new Vector3(-5, 0, 0)), 1e-6f);
        Assert.Equal(0f, Spatial.Pan(Vector3.Zero, forward, new Vector3(0, 0, -5)), 1e-6f);
    }

    [Fact]
    public void SourceAtTheListenerIsCentred()
    {
        Assert.Equal(0f, Spatial.Pan(new Vector3(1, 2, 3), -Vector3.UnitZ, new Vector3(1, 2, 3)));
    }

    [Fact]
    public void ListenerLookingStraightDownIsCentred()
    {
        // There is no "right" when facing along the up axis, so nothing can be panned.
        Assert.Equal(0f, Spatial.Pan(Vector3.Zero, -Vector3.UnitY, new Vector3(5, 0, 0)));
    }

    [Fact]
    public void EqualPowerCentreIsHalfPowerEachSide()
    {
        (float left, float right) = Spatial.EqualPower(0);

        Assert.Equal(0.7071f, left, 1e-4f);
        Assert.Equal(0.7071f, right, 1e-4f);
    }

    [Fact]
    public void EqualPowerFullLeftIsOnlyLeft()
    {
        (float left, float right) = Spatial.EqualPower(-1);

        Assert.Equal(1f, left, 1e-6f);
        Assert.Equal(0f, right, 1e-6f);
    }

    // The source is 10 m ahead of a listener at the origin; 34.3 m/s is a tenth of the speed of sound.
    private static readonly Vector3 Ahead = new(0, 0, -10);

    [Fact]
    public void DopplerOfASourceComingCloserIsHigher()
    {
        // 343 / (343 - 34.3)
        Assert.Equal(1.11111f, Spatial.Doppler(Vector3.Zero, Vector3.Zero, Ahead, new Vector3(0, 0, 34.3f)), 1e-4f);
    }

    [Fact]
    public void DopplerOfASourceGoingAwayIsLower()
    {
        // 343 / (343 + 34.3)
        Assert.Equal(0.90909f, Spatial.Doppler(Vector3.Zero, Vector3.Zero, Ahead, new Vector3(0, 0, -34.3f)), 1e-4f);
    }

    [Fact]
    public void DopplerOfAListenerMovingTowardsTheSourceIsHigher()
    {
        // (343 + 34.3) / 343
        Assert.Equal(1.1f, Spatial.Doppler(Vector3.Zero, new Vector3(0, 0, -34.3f), Ahead, Vector3.Zero), 1e-4f);
    }

    [Fact]
    public void DopplerOfAStillSourceAndListenerIsOne()
    {
        Assert.Equal(1f, Spatial.Doppler(Vector3.Zero, Vector3.Zero, Ahead, Vector3.Zero));
    }

    [Fact]
    public void DopplerOfASourceCrossingInFrontIsOne()
    {
        // Moving across the line between them, it comes no closer.
        Assert.Equal(1f, Spatial.Doppler(Vector3.Zero, Vector3.Zero, Ahead, new Vector3(30, 0, 0)), 1e-6f);
    }

    [Fact]
    public void DopplerStaysWithinAnOctaveEachWay()
    {
        Assert.Equal(2f, Spatial.Doppler(Vector3.Zero, Vector3.Zero, Ahead, new Vector3(0, 0, 500)));
        Assert.Equal(0.5f, Spatial.Doppler(Vector3.Zero, Vector3.Zero, Ahead, new Vector3(0, 0, -500)));
        Assert.Equal(0.5f, Spatial.Doppler(Vector3.Zero, new Vector3(0, 0, 500), Ahead, Vector3.Zero));
    }

    [Fact]
    public void DopplerAtTheListenerIsOne()
    {
        Assert.Equal(1f, Spatial.Doppler(Vector3.One, Vector3.Zero, Vector3.One, new Vector3(0, 0, 50)));
    }
}
