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
    // Light keys, apart from the model's key of 0.
    private const int LampKey = 1;
    private const int RoomKey = 2;
    private const int ShadeKey = 3;

    // The model keeps Blender's origin (keepOrigin in its sidecar), so a point from
    // art/courtyard.py is the same here, with Blender's z as y and its y as -z.
    // Just under the bulb in the lamp's shade (build_lamp).
    private static readonly Vector3 LampBulb = new(4.55f, 3.8f, -3.5f);

    // A lamp hung near the back of the room behind the window (build_window), a little to the
    // left, so its walls fall off from warm to dark.
    private static readonly Vector3 RoomLamp = new(1.6f, 3.9f, -7.4f);

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
        view.Camera(position: new Vector3(5.41f, 1.56f, 4.49f), lookAt: new Vector3(1.01f, 1.36f, -7.11f), fieldOfView: 0.85f);

        // Mid-afternoon. The sun is high on the left and a little in front of the wall, still
        // low enough to rake across the bricks and throw shadows to the right.
        view.Sun(direction: new Vector3(-0.75f, 0.5f, 0.42f), color: new Vector3(5.0f, 4.7f, 4.2f));
        // A clear blue sky, paler at the horizon, so the shade is cooler than the sunlight.
        view.Sky(new SkySettings
        {
            Zenith = new Vector3(0.2f, 0.38f, 0.8f),
            Horizon = new Vector3(0.75f, 0.8f, 0.85f),
            Ground = new Vector3(0.2f, 0.17f, 0.14f),
            Brightness = 1.0f,
        });
        // A sun close to its real size, so the tree out of frame to the left, 10 m up-sun of
        // the wall, dapples it with crisp leafy shade rather than one soft patch.
        view.Shadows(7f, 3, sunSize: 0.02f);

        // The lamp is on, shining down from under its shade onto the wall and the path.
        view.SpotLight(LampKey, LampBulb, direction: -Vector3.UnitY, color: new Vector3(10f, 6.8f, 3.4f),
            range: 8f, innerAngle: 0.5f, outerAngle: 1.3f, radius: 0.12f);
        // The spot only shines down, so the bulb's light on the pale inside of its own shade
        // is a small light of its own. It reaches no further than the shade's rim.
        view.PointLight(ShadeKey, LampBulb, color: new Vector3(0.5f, 0.34f, 0.17f), range: 0.7f, radius: 0.08f);
        // Someone is home: a warm light in the room, seen through the old glass.
        view.PointLight(RoomKey, RoomLamp, color: new Vector3(2.2f, 1.2f, 0.45f), range: 6f, radius: 0.25f);

        // A cool lift in the shade, a touch of warmth in the light, and a little bloom round the lamp.
        view.Finish(new PostSettings
        {
            Exposure = 0.1f,
            Contrast = 0.1f,
            Saturation = 0.05f,
            Vignette = 0.35f,
            Sharpen = 0.2f,
            Bloom = 0.06f,
            Lift = new Vector3(0f, 0.005f, 0.015f),
            Gain = new Vector3(0.01f, 0.005f, -0.005f),
        });
        view.Model(0, _courtyard, Matrix4x4.Identity);
    }
}
