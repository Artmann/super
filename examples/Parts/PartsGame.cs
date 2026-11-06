using System.Numerics;
using Super;

namespace Parts;

/// <summary>
/// A 3 by 3 grid of figures made from one model of eight named meshes. Each figure shows one body, one head and,
/// on six of them, a hat, picked with a part mask made once at load. Space shows every part of every figure at
/// once, as a pile, and the <c>unmasked</c> script presses it for the before picture.
/// </summary>
public sealed class PartsGame(Content content, Input input) : IGame
{
    private const int GridSize = 3;
    private const float Spacing = 2f;
    private const float TurnsPerSecond = 0.08f;
    private const int GroundKey = 100;

    private static readonly string[] Bodies = ["Body_Red", "Body_Green", "Body_Blue"];
    private static readonly string[] Heads = ["Head_Round", "Head_Square", "Head_Cone"];

    private readonly PartMask[] _masks = new PartMask[GridSize * GridSize];
    private ModelRef _figure;
    private bool _showEveryPart;
    private float _turns;
    private float _previousTurns;

    /// <summary>The model every figure is drawn from.</summary>
    public ModelRef Figure => _figure;

    /// <summary>The part names of <see cref="Figure"/>, in the order they were cooked.</summary>
    public IReadOnlyList<string> FigureParts { get; private set; } = [];

    /// <summary>Whether Space has turned every part on, so each figure is drawn whole.</summary>
    public bool ShowingEveryPart => _showEveryPart;

    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        ["unmasked"] = new InputScript().Press(Key.Space),
    };

    public void Load()
    {
        _figure = content.Model("parts/figures.gltf");
        FigureParts = content.PartNames(_figure);

        for (int body = 0; body < GridSize; body++)
        {
            for (int head = 0; head < GridSize; head++)
            {
                // The hat is a different one on each side of the diagonal, and none on the diagonal.
                string? hat = body == head ? null : body < head ? "Hat_Top" : "Hat_Cap";
                string[] names = hat is null ? [Bodies[body], Heads[head]] : [Bodies[body], Heads[head], hat];
                _masks[body * GridSize + head] = content.Parts(_figure, names);
            }
        }
    }

    public void Update(GameTime tick)
    {
        if (input.Pressed(Key.Space))
        {
            _showEveryPart = !_showEveryPart;
        }

        _previousTurns = _turns;
        _turns += TurnsPerSecond * tick.TickSeconds;
    }

    public void Draw(View view)
    {
        view.Camera(position: new Vector3(0f, 3.6f, 6.8f), lookAt: new Vector3(0f, 0.9f, 0f));
        view.Sun(new Vector3(0.5f, 1f, 0.35f), new Vector3(2.6f, 2.5f, 2.3f), shadows: true);
        view.Shadows(12f);

        view.Model(GroundKey, ModelRef.Cube, Matrix4x4.CreateScale(14f, 0.2f, 14f) * Matrix4x4.CreateTranslation(0f, -0.1f, 0f),
            new Vector3(0.42f, 0.42f, 0.38f));

        for (int body = 0; body < GridSize; body++)
        {
            for (int head = 0; head < GridSize; head++)
            {
                int index = body * GridSize + head;
                float x = (body - 1) * Spacing;
                float z = (head - 1) * Spacing;
                var place = Matrix4x4.CreateTranslation(x, 0f, z);

                // Each figure turns at its own pace, a little out of step with its neighbours.
                float offset = index * 0.6f;
                var turn = Spin(_previousTurns + offset) * place;
                var now = Spin(_turns + offset) * place;
                var transform = view.Smooth(turn, now);

                if (_showEveryPart)
                {
                    view.Model(index, _figure, transform);
                }
                else
                {
                    view.Model(index, _figure, transform, _masks[index]);
                }
            }
        }
    }

    private static Matrix4x4 Spin(float turns) => Matrix4x4.CreateRotationY(turns * MathF.Tau);
}
