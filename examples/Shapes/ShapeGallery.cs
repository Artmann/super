using System.Numerics;
using Super;

namespace ShapesExample;

/// <summary>
/// Every built-in shape in the back row and shapes made with <see cref="Shapes"/> in the
/// front, turning slowly on a chequered floor whose texture tiles in metres.
/// </summary>
public sealed class ShapeGallery(Content content) : IGame
{
    private const float TurnsPerSecond = 0.05f;
    private const float Spacing = 1.6f;

    private readonly List<Exhibit> _exhibits = [];
    private ModelRef _floor;
    private float _angle;
    private float _previousAngle;

    /// <summary>A shape on show: what it is, where it stands, how it's tipped, its colour, and whether it turns.</summary>
    private readonly record struct Exhibit(ModelRef Model, Vector3 Position, Matrix4x4 Tilt, Vector3 Color, bool Turns = true);

    public void Load()
    {
        var chequer = content.Texture(Chequer(64, 32));
        _floor = content.Mesh(Shapes.Plane(new Vector2(30f, 30f), surface: new Surface { ColorTexture = chequer, Color = Vector3.One, Roughness = 0.8f }, uvPerMetre: 0.5f));

        // The built-in shapes are one unit wide and centred on zero, so each is lifted by half its height.
        Row(
            z: -1.2f,
            new Exhibit(ModelRef.Cube, new Vector3(0f, 0.5f, 0f), Matrix4x4.Identity, new Vector3(0.8f, 0.25f, 0.2f)),
            new Exhibit(ModelRef.Sphere, new Vector3(0f, 0.5f, 0f), Matrix4x4.Identity, new Vector3(0.9f, 0.6f, 0.15f)),
            new Exhibit(ModelRef.Capsule, new Vector3(0f, 1f, 0f), Matrix4x4.Identity, new Vector3(0.85f, 0.8f, 0.3f)),
            new Exhibit(ModelRef.Cylinder, new Vector3(0f, 0.5f, 0f), Matrix4x4.Identity, new Vector3(0.35f, 0.7f, 0.3f)),
            new Exhibit(ModelRef.Cone, new Vector3(0f, 0.5f, 0f), Matrix4x4.Identity, new Vector3(0.2f, 0.6f, 0.7f)),
            new Exhibit(ModelRef.Torus, new Vector3(0f, 0.5f, 0f), Matrix4x4.CreateRotationX(1.1f), new Vector3(0.3f, 0.35f, 0.8f)),
            new Exhibit(ModelRef.Quad, new Vector3(0f, 0.5f, 0f), Matrix4x4.Identity, new Vector3(0.6f, 0.3f, 0.75f), Turns: false));

        // Shapes made in code: other sizes, a low-poly look and hex tiles.
        Row(
            z: 1.2f,
            Made(Shapes.Hemisphere(0.55f), 0f, new Vector3(0.75f, 0.45f, 0.35f)),
            Made(Shapes.Cylinder(topRadius: 0.25f, bottomRadius: 0.5f, height: 1.2f), 0.6f, new Vector3(0.6f, 0.4f, 0.65f)),
            Made(Shapes.Flat(Shapes.Sphere(0.5f, segments: 8, rings: 5)), 0.5f, new Vector3(0.3f, 0.65f, 0.55f)),
            Made(Shapes.Prism(5, 0.5f, 0.8f), 0.4f, new Vector3(0.8f, 0.55f, 0.25f)),
            Made(Shapes.Hex(0.55f, 0.25f), 0.125f, new Vector3(0.4f, 0.6f, 0.25f)),
            Made(Shapes.Box(new Vector3(1f, 0.4f, 0.6f)), 0.2f, new Vector3(0.65f, 0.3f, 0.3f)),
            Made(Shapes.Capsule(radius: 0.25f, height: 1.3f, segments: 24), 0.65f, new Vector3(0.85f, 0.75f, 0.4f)));
    }

    public void Update(GameTime time)
    {
        _previousAngle = _angle;
        _angle += MathF.Tau * TurnsPerSecond * time.TickSeconds;
    }

    public void Draw(View view)
    {
        view.Camera(position: new Vector3(0f, 3.4f, 8.5f), lookAt: new Vector3(0f, 0.4f, 0f), fieldOfView: 0.75f);
        view.Sun(direction: new Vector3(-0.6f, 1.4f, 0.9f), color: new Vector3(3f));
        view.Shadows(15f, 2);
        view.Model(0, _floor, Matrix4x4.Identity);

        float angle = float.Lerp(_previousAngle, _angle, view.Blend);
        for (int i = 0; i < _exhibits.Count; i++)
        {
            var exhibit = _exhibits[i];
            var turn = exhibit.Turns ? Matrix4x4.CreateRotationY(angle + i) : Matrix4x4.Identity;
            view.Model(i + 1, exhibit.Model, exhibit.Tilt * turn * Matrix4x4.CreateTranslation(exhibit.Position), exhibit.Color);
        }
    }

    private Exhibit Made(GeneratedMesh mesh, float lift, Vector3 color, Matrix4x4? tilt = null) =>
        new(content.Mesh(mesh), new Vector3(0f, lift, 0f), tilt ?? Matrix4x4.Identity, color);

    /// <summary>Spreads a row out along x, centred, at this depth.</summary>
    private void Row(float z, params ReadOnlySpan<Exhibit> exhibits)
    {
        float start = -(exhibits.Length - 1) * Spacing * 0.5f;
        for (int i = 0; i < exhibits.Length; i++)
        {
            _exhibits.Add(exhibits[i] with { Position = exhibits[i].Position + new Vector3(start + i * Spacing, 0f, z) });
        }
    }

    /// <summary>Light and dark squares, <paramref name="square"/> pixels wide.</summary>
    private static GeneratedTexture Chequer(int size, int square)
    {
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                byte shade = (x / square + y / square) % 2 == 0 ? (byte)200 : (byte)150;
                int at = (y * size + x) * 4;
                pixels[at] = shade;
                pixels[at + 1] = shade;
                pixels[at + 2] = shade;
                pixels[at + 3] = 255;
            }
        }

        return new GeneratedTexture(size, pixels);
    }
}
