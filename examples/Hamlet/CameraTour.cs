using System.Numerics;
using Super;

namespace Hamlet;

/// <summary>Where the camera is at a moment, what it looks at, how wide it sees, and how far indoors it is.</summary>
internal readonly record struct CameraPose(Vector3 Position, Vector3 Target, float FieldOfView, float Indoors);

/// <summary>
/// A minute-long tour that repeats: the camera circles the village high up for forty
/// seconds, then swoops down to a cottage's garden gate, walks in through the door and
/// turns to the window the sun comes through. Each tour visits the same cottage, once by
/// day and once by night, as the day is two tours long. It's worked out from the time
/// alone, so any tick gives the same shot.
/// </summary>
internal sealed class CameraTour
{
    /// <summary>How long one tour lasts, in seconds.</summary>
    public const float Length = 60f;

    private const float OrbitEnd = 40f;
    private const float AtGate = 49f;
    private const float Inside = 53.5f;
    private const float OrbitRadius = 80f;
    private const float OrbitHeight = 34f;
    private const float OrbitSweep = 0.9f * MathF.PI;
    private const float EyeHeight = 1.6f;
    private const float WideView = 50f * MathF.PI / 180f;
    private const float RoomView = 72f * MathF.PI / 180f;

    /// <summary>When in the tour the sun through the window is checked, for choosing the cottage.</summary>
    private const float Visit = 56f;

    private readonly Vector3 _middle;
    private readonly float _orbitEndAngle;
    private readonly Spline _descent;
    private readonly Vector3 _gate;
    private readonly Vector3 _threshold;
    private readonly Vector3 _spot;
    private readonly Vector3 _ahead;
    private readonly Vector3 _window;

    public CameraTour(Village village)
    {
        _middle = new Vector3(0f, village.Height(0f, 0f) + 1f, 0f);
        var sun = DayCycle.SunDirection(Visit);
        Cottage = Choose(village, sun, out _descent);

        var cottage = Cottage;
        var eye = new Vector3(0f, EyeHeight, 0f);
        var window = SunniestWindow(cottage, sun);
        _gate = cottage.ToWorld(cottage.Plan.Door) + eye;
        _threshold = cottage.ToWorld(cottage.Plan.Inside) + eye;
        _spot = cottage.ToWorld(Across(window, cottage.Plan.Inside.Y)) + eye;
        var inward = Vector3.Normalize(_threshold - _gate);
        _ahead = _threshold + new Vector3(inward.X, 0f, inward.Z) * 3f - new Vector3(0f, 0.3f, 0f);
        _window = SunPatch(cottage, window, sun);
        var start = _descent.PointAtDistance(0f);
        _orbitEndAngle = MathF.Atan2(start.X, start.Z);
    }

    /// <summary>The cottage the tour walks into.</summary>
    public Building Cottage { get; }

    public CameraPose Pose(float seconds)
    {
        float t = seconds % Length;
        if (t < OrbitEnd)
        {
            float angle = _orbitEndAngle - OrbitSweep * (1f - t / OrbitEnd);
            return new CameraPose(Orbit(angle), _middle, WideView, 0f);
        }

        if (t < AtGate)
        {
            // Down from the orbit to the garden gate, turning from the village to the door.
            float share = DayCycle.Smooth(OrbitEnd, AtGate, t);
            var position = _descent.PointAtDistance(share * _descent.Length);
            var target = Vector3.Lerp(_middle, _ahead, DayCycle.Smooth(0.1f, 0.7f, share));
            return new CameraPose(position, target, WideView, 0f);
        }

        if (t < Inside)
        {
            // Through the gate, up the garden path and in at the door.
            float share = DayCycle.Smooth(AtGate, Inside, t);
            var position = Vector3.Lerp(_gate, _threshold, share);
            float wide = DayCycle.Smooth(0.4f, 1f, share);
            return new CameraPose(position, _ahead, float.Lerp(WideView, RoomView, wide), DayCycle.Smooth(0.45f, 0.9f, share));
        }

        // Across the room, turning to the window and the patch of sun it throws.
        float walk = DayCycle.Smooth(Inside, Inside + 2.5f, t);
        float turn = DayCycle.Smooth(Inside + 0.5f, Inside + 3.5f, t);
        var look = Vector3.Lerp(_ahead, _window, turn);
        return new CameraPose(Vector3.Lerp(_threshold, _spot, walk), look, RoomView, 1f);
    }

    private Vector3 Orbit(float angle) =>
        _middle + new Vector3(MathF.Sin(angle) * OrbitRadius, OrbitHeight, MathF.Cos(angle) * OrbitRadius);

    /// <summary>
    /// Picks the cottage whose window best faces the sun at the visit, among those the
    /// camera can reach without passing through another building or the ground.
    /// </summary>
    private Building Choose(Village village, Vector3 sun, out Spline descent)
    {
        Building? best = null;
        Spline? bestDescent = null;
        float bestScore = float.NegativeInfinity;
        foreach (var building in village.Buildings)
        {
            if (building.Plan.Name != "cottage")
            {
                continue;
            }

            float score = float.NegativeInfinity;
            var front = building.DirectionToWorld(Vector3.UnitZ);
            foreach (var window in building.Plan.Windows)
            {
                var outward = building.DirectionToWorld(window.Outward);
                if (Vector3.Dot(outward, front) < 0.5f)
                {
                    score = MathF.Max(score, Vector3.Dot(outward, sun));
                }
            }

            var path = Descent(building);
            if (!Clear(village, building, path))
            {
                score -= 10f;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = building;
                bestDescent = path;
            }
        }

        descent = bestDescent!;
        return best!;
    }

    /// <summary>
    /// The way down for a cottage: from high above, out beyond its gate, to the gate at eye
    /// height. The orbit ends where this starts.
    /// </summary>
    private Spline Descent(Building cottage)
    {
        var gate = cottage.ToWorld(cottage.Plan.Door) + new Vector3(0f, EyeHeight, 0f);
        var front = cottage.DirectionToWorld(Vector3.UnitZ);
        var hover = gate + front * 16f + new Vector3(0f, 9f, 0f);
        var near = gate + front * 5f + new Vector3(0f, 1.2f, 0f);
        var outward = Vector3.Normalize(new Vector3(hover.X - _middle.X, 0f, hover.Z - _middle.Z));
        var start = _middle + outward * OrbitRadius + new Vector3(0f, OrbitHeight, 0f);
        var mid = Vector3.Lerp(start, hover, 0.55f) + new Vector3(0f, 4f, 0f);
        return Spline.Through([start, mid, hover, near, gate]);
    }

    /// <summary>Whether the camera stays out of other buildings and above the ground all the way in.</summary>
    private static bool Clear(Village village, Building cottage, Spline path)
    {
        for (float s = 0f; s <= path.Length; s += 0.5f)
        {
            var point = path.PointAtDistance(s);
            if (point.Y < village.Height(point.X, point.Z) + 1.2f)
            {
                return false;
            }

            foreach (var other in village.Buildings)
            {
                if (other != cottage && other.Contains(point, 1f))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The side or back window that faces the sun most squarely at the visit.</summary>
    private static Window SunniestWindow(Building cottage, Vector3 sun)
    {
        var front = cottage.DirectionToWorld(Vector3.UnitZ);
        Window? best = null;
        float bestFacing = float.NegativeInfinity;
        foreach (var window in cottage.Plan.Windows)
        {
            var outward = cottage.DirectionToWorld(window.Outward);
            float facing = Vector3.Dot(outward, sun);
            if (Vector3.Dot(outward, front) < 0.5f && facing > bestFacing)
            {
                bestFacing = facing;
                best = window;
            }
        }

        return best!.Value;
    }

    /// <summary>
    /// Where to stand to see a window: across the room from it, a little towards the middle,
    /// with the floor at the given height. In the building's own space.
    /// </summary>
    private static Vector3 Across(Window window, float floor)
    {
        var away = -window.Outward;
        float along = 0.5f;
        float x = MathF.Abs(away.X) > 0.5f ? away.X * 1.4f : window.Middle.X * along;
        float z = MathF.Abs(away.Z) > 0.5f ? away.Z * 1.1f : window.Middle.Z * along;
        return new Vector3(x, floor, z);
    }

    /// <summary>
    /// A point between a window and the patch of light the sun throws through it onto the
    /// floor, for the camera to end on.
    /// </summary>
    private static Vector3 SunPatch(Building cottage, Window window, Vector3 sun)
    {
        var middle = cottage.ToWorld(window.Middle);
        var floor = cottage.ToWorld(Vector3.Zero).Y + 0.15f;
        var across = sun.Y > 0.05f ? (middle.Y - floor) / sun.Y : 2f;
        var patch = middle - sun * MathF.Min(across, 3f);
        return Vector3.Lerp(middle, patch, 0.55f);
    }
}
