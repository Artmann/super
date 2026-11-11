using System.Numerics;
using Super;

namespace Hamlet;

/// <summary>
/// A village on a hill through a two-minute day, lit only by today's real-time lights: the
/// sun and the moon, a lamp or two in every building, a forge's fire, and the glow from
/// lit windows. At dusk the lamps come on one by one and flicker. The camera circles the
/// village, then walks into a cottage to see the window light by day and the lamp by night.
/// R makes a new village from the next seed.
/// </summary>
public sealed class HamletGame(GameRandom random, Input input, Content content) : IGame
{
    private const int BuildingKey = 1_000;
    private const int GroundKey = 100;
    private const int CartKey = 90;
    private const int LightKey = 100_000;
    private const int CartLightKey = 99_000;
    private const int KeysPerBuilding = 64;
    private const int PaneKeys = 32;

    private Palette _palette = null!;
    private Kinds _kinds = null!;
    private Village _village = null!;
    private Ground _ground = null!;
    private CameraTour _tour = null!;
    private Cart _cart = null!;
    private ModelRef _pane;
    private Vector3 _cartColor;
    private int _villages;
    private float _previousSeconds;
    private float _seconds;

    public void Load()
    {
        _palette = new Palette(content);
        _kinds = new Kinds(content, _palette);
        _cart = new Cart(content, _palette);
        _pane = content.Mesh(Shapes.Quad(Vector2.One, _palette.Pane));
        Build(random.Seed);
    }

    public void Update(GameTime time)
    {
        if (input.Pressed(Key.R))
        {
            _villages++;
            _ground.Unload(content);
            Build(random.Seed + _villages);
        }

        _previousSeconds = _seconds;
        _seconds = time.Tick * time.TickSeconds;
    }

    public void Draw(View view)
    {
        float seconds = float.Lerp(_previousSeconds, _seconds, view.Blend);
        var pose = _tour.Pose(seconds);
        view.Camera(pose.Position, pose.Target, pose.FieldOfView);
        DayCycle.Light(view, seconds, pose.Indoors);
        if (pose.Indoors > 0.5f)
        {
            // Close up indoors: sharper shadows, and the room's corners hide more of the sky.
            view.Shadows(18f, 2);
            view.AmbientOcclusion(1.5f, 1f);
        }
        else
        {
            view.Shadows(90f, 3);
        }

        for (int i = 0; i < _ground.Models.Count; i++)
        {
            if (content.IsReady(_ground.Models[i]))
            {
                view.Model(GroundKey + i, _ground.Models[i], Matrix4x4.Identity);
            }
        }

        float elevation = DayCycle.Elevation(seconds);
        foreach (var building in _village.Buildings)
        {
            DrawBuilding(view, building, seconds, elevation, inside: pose.Indoors > 0.5f && building == _tour.Cottage);
        }

        var cart = Cart.Placement(_village, seconds);
        _cart.Draw(view, CartKey, cart, seconds, _cartColor);
        float cartLamp = 1f - DayCycle.Smooth(-0.06f, 0.02f, elevation);
        if (cartLamp > 0.01f)
        {
            var lantern = Vector3.Transform(Cart.Lantern, cart);
            float flicker = Flicker(seconds, 7f);
            view.SpotLight(CartLightKey, lantern, -Vector3.UnitY, Palette.Lamplight * 3f * cartLamp * flicker, 8f, 0.6f, 1.35f);
        }
    }

    private void Build(int seed)
    {
        _village = Village.Generate(seed, _kinds);
        _ground = new Ground(content, _village);
        _tour = new CameraTour(_village);        _cartColor = Palette.Accents[(seed % Palette.Accents.Length + Palette.Accents.Length) % Palette.Accents.Length];
    }

    /// <summary>
    /// Draws a building, its lamps and its lit windows. <paramref name="inside"/> is true when
    /// the camera stands in it: the glowing panes are left out then, as a pane is seen from
    /// both sides and would hide the night outside.
    /// </summary>
    private void DrawBuilding(View view, Building building, float seconds, float elevation, bool inside)
    {
        int key = BuildingKey + building.Index * KeysPerBuilding;
        int lightKey = LightKey + building.Index * KeysPerBuilding;
        float level = 1f - DayCycle.Smooth(building.Dusk - 0.04f, building.Dusk + 0.04f, elevation);
        float flicker = Flicker(seconds, building.Phase);
        float fire = 1f + 0.18f * MathF.Sin(seconds * 9f + building.Phase) + 0.1f * MathF.Sin(seconds * 21f + building.Phase * 1.3f);
        var plan = building.Plan;

        for (int p = 0; p < plan.Parts.Length; p++)
        {
            var part = plan.Parts[p];
            Vector3? tint = part.Tint switch
            {
                PartTint.Roof => building.RoofColor,
                PartTint.Accent => building.AccentColor,
                PartTint.Lamps => new Vector3(1f + level * flicker),
                PartTint.Fire => new Vector3(1f + fire),
                _ => null,
            };
            view.Model(key + p, part.Model, building.Placement, tint);
        }

        for (int j = 0; j < plan.Lamps.Length; j++)
        {
            var lamp = plan.Lamps[j];
            float strength = lamp.Fire ? fire : level * flicker;
            if (strength > 0.01f)
            {
                view.SpotLight(
                    lightKey + j,
                    building.ToWorld(lamp.Position),
                    building.DirectionToWorld(lamp.Direction),
                    lamp.Color * strength,
                    lamp.Range,
                    lamp.InnerAngle,
                    lamp.OuterAngle);
            }
        }

        if (level <= 0.01f)
        {
            return;
        }

        // Lit windows: a glowing pane in each, and a little warm light thrown out onto the ground.
        for (int w = 0; w < plan.Windows.Length; w++)
        {
            var window = plan.Windows[w];
            var middle = window.Middle - window.Outward * 0.09f;
            var frame = Matrix4x4.CreateScale(window.Size.X, window.Size.Y, 1f) * PlanBuilder.Facing(middle, window.Outward) * building.Placement;
            if (!inside)
            {
                view.Model(key + PaneKeys + w, _pane, frame, new Vector3(1f + level * flicker));
            }

            var outward = building.DirectionToWorld(window.Outward);
            view.SpotLight(
                lightKey + PaneKeys + w,
                building.ToWorld(window.Middle + window.Outward * 0.25f),
                outward + new Vector3(0f, -0.6f, 0f),
                Palette.Lamplight * 1.6f * level * flicker,
                7f,
                0.5f,
                1.2f,
                shadows: false,
                volumetric: 0.3f);
        }
    }

    /// <summary>A lamp's flame wavering, about 1, different for every building.</summary>
    private static float Flicker(float seconds, float phase) =>
        1f + 0.07f * MathF.Sin(seconds * 11f + phase) + 0.05f * MathF.Sin(seconds * 23f + phase * 1.7f);
}
