using System.Numerics;
using Rendition;
using Super;

namespace Gallery;

/// <summary>The lamp room's three hanging lamps and the brass sphere that rolls back and forth under them.</summary>
internal sealed class LampRoom
{
    private const int LightKey = 100;
    private const float BrassRadius = 0.35f;

    private readonly ModelRef _brass;

    public LampRoom(Content content)
    {
        _brass = content.Mesh(Shapes.Sphere(BrassRadius, 64, 32, new Surface { Color = new Vector3(0.95f, 0.72f, 0.38f), Metallic = 1f, Roughness = 0.22f }));
    }

    public void Draw(View view, float seconds)
    {
        // Three hanging lamps with big glowing discs, so their shadows are soft and wide.
        var lamps = GalleryPlan.Lamps;
        for (int i = 0; i < lamps.Length; i++)
        {
            var disc = lamps[i];
            // Lamp 3 is baked, so its soft shadows cost nothing. Lamps 1 and 2 stay mixed with real-time
            // shadows, so the rolling sphere still shades the floor under them.
            var mode = i == 2 ? LightMode.Baked : LightMode.Mixed;
            view.Lights.Point(LightKey + i, new PointLight(disc - new Vector3(0f, 0.15f, 0f), new Vector3(2.2f, 1.6f, 0.95f), 5.5f) { Radius = 0.35f, Mode = mode });
        }

        // The brass sphere rolls back and forth under the lamps, so their soft shadows swing round it.
        float x = -6.5f + 2.6f * MathF.Sin(seconds * 0.45f);
        var roll = Matrix4x4.CreateRotationZ(-x / BrassRadius);
        view.Model(GalleryPlan.DynamicKey + 10, _brass, roll * Matrix4x4.CreateTranslation(x, BrassRadius, -24.2f));
    }
}
