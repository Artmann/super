using System.Numerics;
using Super;

namespace Spinner;

public record Cube(string Id, float TurnsPerSecond, Vector3 Glow);

public sealed class SpinnerGame(Content content) : IGame
{
    private ModelRef _model;
    private ModelRef _stand;

    public Cube Cube { get; private set; } = null!;

    public float Angle { get; private set; }

    public void Load()
    {
        // From content/cubes/red.json, which the build cooked.
        Cube = content.LoadAll<Cube>("cubes")["red"];

        // Shaders/glow.frag, which the build compiled.
        _model = content.Mesh(Shapes.Box(new Vector3(1f), new Surface
        {
            Color = new Vector3(0.8f, 0.2f, 0.15f),
            Roughness = 0.5f,
            Shader = content.Shader("glow"),
            Parameters = [new Vector4(Cube.Glow, 0f)],
        }));

        // From content/models/stand.obj, which the build cooked with the cook tool's Assimp.
        _stand = content.Model("models/stand.obj");
    }

    public void Update(GameTime time) => Angle += time.TickSeconds * Cube.TurnsPerSecond * MathF.Tau;

    public void Draw(View view)
    {
        view.Camera(position: new Vector3(0, 2, 5), lookAt: Vector3.Zero);
        view.Sun(direction: new Vector3(1, 2, 1), color: new Vector3(3f));
        view.Model(key: 1, _model, Matrix4x4.CreateRotationY(Angle));
        view.Model(key: 2, _stand, Matrix4x4.CreateTranslation(0f, -0.6f, 0f));
    }
}
