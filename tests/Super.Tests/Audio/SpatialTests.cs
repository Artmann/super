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
}
