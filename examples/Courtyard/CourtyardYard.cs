using System.Numerics;
using Rendition;
using Super;

namespace Courtyard;

/// <summary>
/// The yard and how it is lit and finished, as entities: the camera, the model, the sun and its shadows, the sky and its
/// fog, the lantern and the room's light, the camera's finish, the baked space, the reflection probe, the puddle level
/// and the wind.
/// </summary>
public static class CourtyardYard
{
    // The model keeps Blender's origin (keepOrigin in its sidecar), so a point from
    // art/courtyard.py is the same here, with Blender's z as y and its y as -z.
    // The middle of the bulb in the wall lantern (build_lamp), 0.31 m out from the wall.
    private static readonly Vector3 LampBulb = new(4.7f, 3.95f, -3.688f);

    // A lamp hung near the back of the room behind the window (build_window), a little to the
    // left, so its walls fall off from warm to dark.
    private static readonly Vector3 RoomLamp = new(1.6f, 3.9f, -7.4f);

    /// <summary>Spawns the camera, the <paramref name="yard"/> model, the daylight, the lamps, the finish and the surroundings.</summary>
    public static void Spawn(World world, ModelRef yard)
    {
        // Standing back at eye height, off to the right and turned left towards the window, as
        // in the reference photo: the low wall, which runs out square from the building, closes
        // the left of the frame, the path runs in to the puddle and the deck fills the right.
        world.Spawn(new Camera(new Vector3(5.41f, 1.56f, 4.49f), new Vector3(1.01f, 1.36f, -7.11f), 0.85f));

        // The model never moves, so it is static and its light is baked: the sun, which stays Mixed so the real-time
        // cascades still shadow what is close to the camera, and the room lamp, which shines out through the window
        // glass. The grass, ivy and leaves are part of the model and are lightmapped with the rest. See the README.
        world.Spawn(Transform.Identity, new Model(yard), new Static());

        Daylight(world);
        Lamps(world);
        Surroundings(world);
        world.Spawn(GradePicture());
    }

    /// <summary>The baked space, the reflection probe, the puddles and the wind.</summary>
    private static void Surroundings(World world)
    {
        // The yard is one baked space, "courtyard", over the whole model with a little to spare. The decals (the damp,
        // the drip and the rust) stay live.
        world.Spawn(new BakedLighting("courtyard", new Vector3(-16.5f, -0.5f, -18.75f), new Vector3(14.5f, 10f, 12.5f), 1.6f));

        // A reflection probe at head height in the middle of the yard, so the window and the
        // puddle mirror the wall, the deck and the house rather than only the sky. Its box runs
        // from (-3, -1, -10) to (10, 6, 7) in the world.
        world.Spawn(
            Transform.At(new Vector3(3.5f, 1.6f, -1.5f)),
            new ReflectionCapture(BoxMin: new(-6.5f, -2.6f, -8.5f), BoxMax: new(6.5f, 4.4f, 8.5f)));

        // Water stands 4.5 cm below the yard's zero, in the hollow the script dug in the mud, so
        // the hollow fills, the deepest boot prints hold a little, and the mud darkens for the
        // last 3.5 cm down to it. A wider band would darken the flagstones round the puddle too.
        world.Spawn(new Puddles(-0.045f, Band: 0.035f));

        // A fresh breeze across the yard sways the grass, and a little of it reaches the ivy and the tree's crown,
        // whose materials sway less in the cook sidecar.
        world.Spawn(new Wind(new Vector3(0.6f, 0f, -0.3f), 0.35f));
    }

    /// <summary>The sun, the sky, the shadows and the ambient occlusion.</summary>
    private static void Daylight(World world)
    {
        world.Spawn(
            // Mid-afternoon. The sun is high on the left and a little in front of the wall, still
            // low enough to rake across the bricks and throw shadows to the right.
            new Sun(new Vector3(-0.75f, 0.5f, 0.42f), new Vector3(7.2f, 6.7f, 5.8f)),
            // A clear day's sky, worked out from the sun: brighter towards it, deeper blue overhead
            // and paler at the horizon, so the shade is cooler than the sunlight. It is at its full
            // brightness, so the shade is lit by the whole open sky and stays readable, though still
            // a clear step darker than the sun. Zenith and Horizon are what it falls back to with
            // Turbidity at 0.
            new Sky(new SkySettings
            {
                Zenith = new Vector3(0.2f, 0.38f, 0.8f),
                Horizon = new Vector3(0.75f, 0.8f, 0.85f),
                Ground = new Vector3(0.2f, 0.17f, 0.14f),
                Brightness = 1f,
                Turbidity = 2.5f,
                // A light fog, thickest on the ground, so the far wall sits a touch back in the air
                // behind the near flagstones. It glows where it looks towards the sun.
                Fog = new FogSettings(Density: 0.004f, Height: -0.1f, Falloff: 0.15f, SunScatter: 0.6f),
            }),
            // A sun close to its real size, so the tree out of frame to the left, 10 m up-sun of
            // the wall, dapples it with crisp leafy shade rather than one soft patch. Contact
            // shadows seat the stones, the barrel and the sacks on the ground.
            new Shadows(7f, 3, SunSize: 0.02f, Contact: 0.3f),
            // Ambient occlusion over 0.8 m, so the corners and the foot of each wall darken where
            // little of the sky reaches.
            new AmbientOcclusion(0.8f, 1f));
    }

    /// <summary>The wall lantern and the lamp in the room.</summary>
    private static void Lamps(World world)
    {
        // The lantern is on, shining every way through its glass. In daylight it only warms
        // the wall just round it. It casts no shadows: the light sits inside the bulb and the lantern round it, and
        // they would hide it.
        world.Spawn(new PointLight(LampBulb, new Vector3(0.8f, 0.54f, 0.27f), 4f) { Radius = 0.15f, Shadows = false });
        // Someone is home: a warm light in the room, seen through the old glass.
        world.Spawn(new PointLight(RoomLamp, new Vector3(2.2f, 1.2f, 0.45f), 6f) { Radius = 0.25f });
    }

    /// <summary>
    /// The camera meters the courtyard and sets the brightness itself, then adds a little over a stop: AgX's Punchy
    /// look, the default, deepens the mid-tones, and the sunlit wall should read bright. A touch more contrast and
    /// saturation for an afternoon sun, a warm white balance, a light vignette and a little bloom round the lamp. The
    /// lens is a 35 mm, focused on the window, 11 m off, and stopped down to f/8, so the flagstones at our feet soften a
    /// little and the window and the wall stay crisp.
    /// </summary>
    private static Finish GradePicture() =>
        new(new PostSettings
        {
            FocusDistance = 11f,
            Aperture = 8f,
            AutoExposure = true,
            Exposure = 1.15f,
            Temperature = 0.08f,
            Contrast = 0.25f,
            Saturation = 0.15f,
            Vignette = 0.3f,
            Sharpen = 0.15f,
            Bloom = 0.05f,
            FocalLength = 35f,
        });
}
