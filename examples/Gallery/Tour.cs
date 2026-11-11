using System.Numerics;
using Super;

namespace Gallery;

/// <summary>
/// The camera's slow walk through the gallery: it holds still in each room, then glides to the
/// next, easing in and out, by way of points in the corridor so it never passes through a wall.
/// It's worked out from time alone, so the same moment always gives the same camera, and it
/// cuts back to the top once it has held on the courtyard. The holds are halfway through at
/// these seconds: overview 5, entrance 22, colour 35.5, whale 50.5, lamps 65.5, chapel 80.5
/// and courtyard 94, which is ticks 150, 660, 1065, 1515, 1965, 2415 and 2820 at 30 a second.
/// </summary>
public sealed class Tour
{
    private readonly List<Stop> _stops = [];

    /// <summary>A place to stand: how long to wait there, then how long to take getting to the next.</summary>
    private readonly record struct Stop(Camera Camera, float Hold, float Move, string? Room);

    public Tour()
    {
        Add(Look(new(24f, 26f, 26f), new(-2f, 0f, -18f), 0.75f), 10f, 6f, "overview");
        Add(Look(new(0f, 1.8f, 17f), new(0f, 1.6f, 0f)), 0f, 3f);
        Add(Look(new(-2.6f, 1.7f, 9.4f), new(0.6f, 1.2f, -24f)), 6f, 3f, "entrance");

        Room(-4.8f, Look(new(-2.7f, 1.6f, -4.8f), GalleryGame.BustAt + new Vector3(0f, 0.25f, 0f)), "colour");
        Room(-14.7f, Look(new(-2.7f, 1.9f, -14.4f), GalleryGame.WhaleAt + new Vector3(0f, 0.5f, 0f)), "whale");
        Room(-25f, Look(new(-2.7f, 1.7f, -25.6f), new(-10f, 0.9f, -24f)), "lamps");
        Room(-36f, Look(new(-2.7f, 1.6f, -36f), new(-9.5f, 2.2f, -36f)), "chapel");

        Add(Look(new(0.3f, 1.7f, -36f), new(0.3f, 1.6f, -44f)), 0f, 3f);
        Add(Look(new(0.2f, 1.7f, -36.8f), new(-0.8f, 1.3f, -50f)), 6f, 0f, "courtyard");
    }

    /// <summary>How long the whole tour takes before it starts again.</summary>
    public float Seconds => _stops.Sum(stop => stop.Hold + stop.Move);

    /// <summary>The second, from the start, halfway through the hold in <paramref name="room"/>.</summary>
    public float MiddleOf(string room)
    {
        float start = 0f;
        foreach (var stop in _stops)
        {
            if (stop.Room == room)
            {
                return start + stop.Hold / 2f;
            }

            start += stop.Hold + stop.Move;
        }

        throw new ArgumentException($"The tour has no room called {room}.", nameof(room));
    }

    /// <summary>Where the camera is <paramref name="seconds"/> into the tour.</summary>
    public Camera At(float seconds)
    {
        float time = seconds % Seconds;
        for (int i = 0; i < _stops.Count; i++)
        {
            var stop = _stops[i];
            if (time < stop.Hold)
            {
                return stop.Camera;
            }

            time -= stop.Hold;
            if (time < stop.Move)
            {
                var next = _stops[(i + 1) % _stops.Count].Camera;
                float amount = time / stop.Move;
                return Camera.Lerp(stop.Camera, next, amount * amount * (3f - 2f * amount));
            }

            time -= stop.Move;
        }

        return _stops[^1].Camera;
    }

    /// <summary>Into a room off the corridor and out again, by the corridor in front of its door.</summary>
    private void Room(float door, Camera inside, string name)
    {
        Add(Look(new(0.6f, 1.7f, door + 3f), new(0.6f, 1.6f, door - 6f)), 0f, 2.5f);
        Add(Look(new(0.6f, 1.7f, door), new(-6f, 1.4f, door)), 0f, 2f);
        Add(inside, 6f, 2f, name);
        Add(Look(new(0.6f, 1.7f, door), new(0.6f, 1.6f, door - 8f)), 0f, 2.5f);
    }

    private void Add(Camera camera, float hold, float move, string? room = null) => _stops.Add(new Stop(camera, hold, move, room));

    private static Camera Look(Vector3 from, Vector3 at, float fieldOfView = 1f) => new(from, at, fieldOfView);
}
