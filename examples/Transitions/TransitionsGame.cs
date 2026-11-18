using System.Numerics;
using Super;
using Super.Ui;
using static TransitionsExample.Stations;

namespace TransitionsExample;

/// <summary>
/// A small plaza with three stations of shapes, warm, blue and green, to move between. It
/// opens with a showreel, a cutscene that uses every transition once, then hands over:
/// the number keys move to the next station with each transition, and Space plays the
/// showreel again. The captions are interface, so they stay sharp through every one.
/// </summary>
public sealed class TransitionsGame(Content content, Input input, Director director, Transitions transitions) : IGame
{
    private static readonly Color Muted = new(0.8f, 0.82f, 0.86f);
    private static readonly Color Panel = new(0.06f, 0.07f, 0.09f, 0.72f);

    /// <summary>Where the camera stands at each station during play.</summary>
    private static readonly Camera[] Spots =
    [
        new(Warm + new Vector3(3.2f, 2.2f, 5.2f), Warm + new Vector3(0f, 0.9f, 0f), 0.8f),
        new(Blue + new Vector3(0f, 3.6f, 7.5f), Blue + new Vector3(0f, 0.8f, 0f), 0.8f),
        new(Green + new Vector3(-3.4f, 1.6f, 4.6f), Green + new Vector3(0f, 1f, 0f), 0.8f),
    ];

    /// <summary>Each key, the transition it plays, and what it's called on screen.</summary>
    private static readonly (Key Key, string Name, Transition Transition)[] Choices =
    [
        (Key.D1, "Cut", Transition.Cut),
        (Key.D2, "Blend", Transition.Blend(1.6f)),
        (Key.D3, "Fade", Transition.Fade(1f)),
        (Key.D4, "Dissolve", Transition.Dissolve(0.8f)),
        (Key.D5, "Wipe", Transition.Wipe(0.8f, WipeDirection.Left)),
        (Key.D6, "Iris", Transition.Iris(1f)),
    ];

    private ModelRef _floor;
    private float _angle;
    private float _previousAngle;
    private int _spot;
    private string _caption = "";
    private bool _showreelOn;

    public void Load()
    {
        _floor = content.Mesh(Shapes.Plane(new Vector2(40f, 24f), surface: new Surface
        {
            ColorTexture = content.Texture(Chequer(64, 32)),
            Color = Vector3.One,
            Roughness = 0.85f,
        }, uvPerMetre: 0.5f));

        PlayShowreel();
    }

    public void Update(GameTime time)
    {
        _previousAngle = _angle;
        _angle += MathF.Tau * 0.12f * time.TickSeconds;

        if (input.Pressed(Key.Space))
        {
            PlayShowreel();
            return;
        }

        if (director.IsPlaying)
        {
            _showreelOn = true;
            return;
        }

        if (_showreelOn)
        {
            // The showreel's last caption names the cut, not the game, so clear it.
            _showreelOn = false;
            _caption = "Your turn";
        }

        foreach (var (key, name, transition) in Choices)
        {
            if (input.Pressed(key))
            {
                _caption = name;
                transitions.Play(transition, () => _spot = (_spot + 1) % Spots.Length);
            }
        }
    }

    public void Draw(View view)
    {
        view.Camera(Spots[_spot]);
        view.Lights.Sun(new Vector3(-0.5f, 1.2f, 0.8f), new Vector3(2.6f, 2.35f, 2f));
        view.Surroundings.Sky(new SkySettings
        {
            Zenith = new Vector3(0.3f, 0.45f, 0.75f),
            Horizon = new Vector3(0.8f, 0.78f, 0.72f),
            Ground = new Vector3(0.3f, 0.28f, 0.25f),
            Brightness = 0.5f,
            HazeStart = 30f,
            HazeEnd = 120f,
        });
        view.Lights.Shadows(16f, cascades: 2);
        view.Model(0, _floor, Matrix4x4.Identity);

        Stations.Draw(view, float.Lerp(_previousAngle, _angle, view.Blend));
    }

    public UiView? Ui
    {
        get
        {
            var keys = new VStack(HorizontalAlignment.Leading, 4f);
            if (director.IsPlaying)
            {
                keys.Add(new Text("Space: play again").Foreground(Muted));
            }
            else
            {
                foreach (var (key, name, _) in Choices)
                {
                    keys.Add(new Text($"{(int)(key - Key.D1) + 1}  {name}").Foreground(Muted));
                }

                keys.Add(new Text("Space  Showreel").Foreground(Muted));
            }

            return new VStack(HorizontalAlignment.Leading, 12f)
            {
                new Text(_caption).Bold().FontSize(34f),
                keys,
            }
            .Padding(18f)
            .Background(Panel)
            .CornerRadius(10f)
            .Padding(28f)
            .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.BottomLeading);
        }
    }

    /// <summary>
    /// Every transition once, each named as it starts. A fade is centred on its cut, so its
    /// caption comes half its length early.
    /// </summary>
    private void PlayShowreel()
    {
        _spot = 0;
        Func<Vector3> blue = () => Blue;
        director.Play(new Cutscene()
            .Then(Shot.Move(3f,
                new Camera(Warm + new Vector3(5f, 3f, 6f), Warm + new Vector3(0f, 0.9f, 0f), 0.8f),
                new Camera(Warm + new Vector3(3f, 1.8f, 4.5f), Warm + new Vector3(0f, 1f, 0f), 0.75f)),
                Transition.Fade(1.2f))
            .At(0f, () => _caption = "Fade in")
            .Then(Shot.Orbit(3f, blue, new OrbitPath(Radius: 6.5f, Height: 2.6f, FromAngle: -0.6f, ToAngle: 0.4f), look: new Vector3(0f, 0.8f, 0f), fieldOfView: 0.8f),
                Transition.Dissolve(1f))
            .At(3f, () => _caption = "Dissolve")
            .Then(Shot.Move(3f,
                new Camera(Green + new Vector3(-4.5f, 1.2f, 4f), Green + new Vector3(0f, 1f, 0f), 0.8f),
                new Camera(Green + new Vector3(-2.5f, 2.4f, 5f), Green + new Vector3(0f, 0.8f, 0f), 0.8f)),
                Transition.Wipe(0.9f, WipeDirection.Right))
            .At(6f, () => _caption = "Wipe")
            .Then(Shot.Hold(3f, new Camera(Warm + new Vector3(-2.2f, 1.4f, 3.8f), Warm + new Vector3(0f, 1f, 0f), 0.7f)),
                Transition.Iris(1.2f))
            .At(9f, () => _caption = "Iris")
            .Then(Shot.Hold(3f, Spots[1]), Transition.Blend(2f))
            .At(12f, () => _caption = "Blend")
            .Then(Shot.Orbit(3f, () => Green, new OrbitPath(Radius: 5.5f, Height: 1.8f, FromAngle: 0.3f, ToAngle: -0.5f), look: new Vector3(0f, 1f, 0f), fieldOfView: 0.8f),
                Transition.Wipe(0.8f, WipeDirection.Down, softness: 0.3f))
            .At(15f, () => _caption = "Soft wipe")
            .Then(Shot.Hold(3f, new Camera(Blue + new Vector3(4f, 1.2f, 4f), Blue + new Vector3(0f, 1.2f, 0f), 0.7f)),
                Transition.Fade(1.2f, Color.White))
            .At(17.4f, () => _caption = "Fade through white")
            .Then(Shot.Hold(2f, Spots[0]))
            .At(21f, () => _caption = "Cut")
            .End(Transition.Fade(1f)));
    }

    /// <summary>Light and dark squares, <paramref name="square"/> pixels wide.</summary>
    private static GeneratedTexture Chequer(int size, int square)
    {
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                byte shade = (x / square + y / square) % 2 == 0 ? (byte)150 : (byte)95;
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
