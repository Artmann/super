using System.Numerics;
using Super;

namespace Courtyard;

/// <summary>
/// A sunlit courtyard modelled in Blender (art/courtyard.py): a brick wall with an arched
/// window, a dry-stone wall, a cobble path through wet mud and a puddle. Its bricks, stone,
/// mud, wood and rust are the material graphs, put on the model by its cook sidecar.
/// </summary>
public sealed class CourtyardGame(Content content) : IGame
{
    private ModelRef _courtyard;

    public void Load()
    {
        _courtyard = content.Model("models/courtyard.glb");
    }

    public void Update(GameTime time)
    {
    }

    public void Draw(View view)
    {
        // Standing on the path at eye height, looking at the window.
        view.Camera(position: new Vector3(2.6f, 1.8f, 8.5f), lookAt: new Vector3(3.2f, 1.3f, -4f), fieldOfView: 0.95f);

        // A warm afternoon sun, low from the front left, so the wall's bricks and the cobbles throw shadows.
        view.Sun(direction: new Vector3(-0.8f, 0.55f, 0.6f), color: new Vector3(4.4f, 3.8f, 3f));
        view.Sky(new SkySettings
        {
            Zenith = new Vector3(0.35f, 0.47f, 0.68f),
            Horizon = new Vector3(0.72f, 0.74f, 0.76f),
            Ground = new Vector3(0.22f, 0.18f, 0.14f),
            Brightness = 1f,
        });
        view.Shadows(9f, 3);
        view.Model(0, _courtyard, Matrix4x4.Identity);
    }
}
