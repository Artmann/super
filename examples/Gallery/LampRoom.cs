using System.Numerics;
using Rendition;
using Super;

namespace Gallery;

/// <summary>The lamp room's three hanging lamps and the brass sphere that rolls back and forth under them.</summary>
internal sealed class LampRoom
{
    private const float BrassRadius = 0.35f;

    private readonly World _world;
    private readonly Entity _brass;

    /// <summary>Hangs the lamps and sets the sphere under them.</summary>
    public LampRoom(World world, Content content)
    {
        _world = world;

        // Three hanging lamps with big glowing discs, so their shadows are soft and wide.
        var lamps = GalleryPlan.Lamps;
        for (int i = 0; i < lamps.Length; i++)
        {
            // Lamp 3 is baked, so its soft shadows cost nothing. Lamps 1 and 2 stay mixed with real-time
            // shadows, so the rolling sphere still shades the floor under them.
            var mode = i == 2 ? LightMode.Baked : LightMode.Mixed;
            world.Spawn(new PointLight(lamps[i] - new Vector3(0f, 0.15f, 0f), new Vector3(2.2f, 1.6f, 0.95f), 5.5f) { Radius = 0.35f, Mode = mode });
        }

        var brass = content.Mesh(Shapes.Sphere(BrassRadius, 64, 32, new Surface { Color = new Vector3(0.95f, 0.72f, 0.38f), Metallic = 1f, Roughness = 0.22f }));
        _brass = world.Spawn(Transform.Identity, new Model(brass));
        Roll(0f);
    }

    /// <summary>Rolls the brass sphere to where it is at <paramref name="seconds"/>, so the lamps' soft shadows swing round it.</summary>
    public void Roll(float seconds)
    {
        float x = -6.5f + 2.6f * MathF.Sin(seconds * 0.45f);
        _world.Get<Transform>(_brass) = new Transform(new Vector3(x, BrassRadius, -24.2f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -x / BrassRadius), Vector3.One);
    }
}
