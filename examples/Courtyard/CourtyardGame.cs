using System.Numerics;
using Rendition;
using Super;

namespace Courtyard;

/// <summary>
/// A sunlit courtyard modelled in Blender (art/courtyard.py): a brick wall with an arched
/// window, a dry-stone wall, a flagstone path through trodden mud and a puddle. Its bricks, stone,
/// mud, wood, rust and window glass are the material graphs, put on the model by its cook sidecar.
/// M turns the materials off and on, to judge the lighting and the shapes on their own.
/// </summary>
public sealed class CourtyardGame(Content content, Input input, Graphics graphics) : IGame
{
    private ModelRef _courtyard;

    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        // The courtyard in plain clay, through the same key a player presses.
        ["plain"] = new InputScript().Press(Key.M).Wait(1),
    };

    public void Load()
    {
        _courtyard = content.Model("models/courtyard.glb");
    }

    public void Update(GameTime time)
    {
        if (input.Pressed(Key.M))
        {
            graphics.Materials = !graphics.Materials;
        }
    }

    public void Draw(View view)
    {
        // Standing back at eye height, off to the right and turned left towards the window, as
        // in the reference photo: the low wall, which runs out square from the building, closes
        // the left of the frame, the path runs in to the puddle and the deck fills the right.
        view.Camera(position: new Vector3(6.4f, 1.7f, 7.6f), lookAt: new Vector3(2f, 1.5f, -4f), fieldOfView: 0.85f);

        // A warm afternoon sun, low from the left and only a little in front of the wall, so it
        // rakes across the bricks: every stone of the plinth has a lit side and a shadow side
        // and throws a long shadow to the right.
        view.Sun(direction: new Vector3(-0.85f, 0.5f, 0.28f), color: new Vector3(6.2f, 5.1f, 3.7f));
        // A dimmer, bluer sky than the sun, so the shade is clearly darker and a little cool.
        view.Sky(new SkySettings
        {
            Zenith = new Vector3(0.3f, 0.43f, 0.7f),
            Horizon = new Vector3(0.7f, 0.72f, 0.76f),
            Ground = new Vector3(0.16f, 0.13f, 0.1f),
            Brightness = 0.6f,
        });
        view.Shadows(9f, 3);
        // A little more contrast and a soft vignette, to hold the eye on the window and the path.
        view.Finish(new PostSettings { Contrast = 0.08f, Saturation = 0.05f, Vignette = 0.3f, Sharpen = 0.2f });
        view.Model(0, _courtyard, Matrix4x4.Identity);
    }
}
