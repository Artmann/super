using System.Numerics;
using Rendition;
using Super;
using Super.Ui;

namespace Hamlet;

/// <summary>
/// A village on a hill through a two-minute day: the sun and the moon, a lamp or two in
/// every building, a forge's fire, and the glow from lit windows. At dusk the lamps come on
/// one by one and flicker. Every building is a baked chunk, so the light bounced round its
/// rooms is baked once per kind and shared by every copy. The camera circles the village,
/// then walks into a cottage to see the window light by day and the lamp by night. R makes
/// a new village from the next seed, P shows the light probes, and 1, 2 and 3 switch between live lighting, the
/// bakes and direct light only.
/// </summary>
public sealed class HamletGame(GameRandom random, Input input, Content content, Graphics graphics) : IGame
{
    /// <summary>The lighting modes that keys 1, 2 and 3 switch between.</summary>
    public enum Lighting
    {
        /// <summary>Key 1: every light live and bounced light from the live probes, as if nothing were baked.</summary>
        Live,

        /// <summary>Key 2: the chunks' bakes where they are fresh.</summary>
        Baked,

        /// <summary>Key 3: direct light only, with no bake and no bounced light.</summary>
        Direct,
    }

    /// <summary>The lightmap density of every building's bake.</summary>
    internal const int TexelsPerMetre = 10;

    private static readonly Color Muted = new(0.8f, 0.82f, 0.86f);
    private static readonly Color Panel = new(0.06f, 0.07f, 0.09f, 0.72f);

    /// <summary>
    /// The first building's first draw key. Building i's parts are drawn under the keys from
    /// <c>BuildingKey + i * KeysPerBuilding</c>, one for each of its plan's parts in order.
    /// </summary>
    internal const int BuildingKey = 1_000;

    /// <summary>How many draw keys each building has.</summary>
    internal const int KeysPerBuilding = 64;

    private const int GroundKey = 100;
    private const int CartKey = 90;
    private const int LightKey = 100_000;
    private const int CartLightKey = 99_000;
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
    private Lighting _lighting = Lighting.Baked;

    /// <summary>The village now shown.</summary>
    internal Village Village => _village;

    /// <summary>The lighting now on.</summary>
    internal Lighting CurrentLighting => _lighting;

    /// <summary>
    /// Scripts for <c>--script</c>: <c>live</c> presses key 1, so a shot shows live lighting; <c>probes</c> presses P,
    /// so a shot shows the light probes.
    /// </summary>
    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        ["live"] = new InputScript().Press(Key.D1).Wait(1),
        ["probes"] = new InputScript().Press(Key.P).Wait(1),
    };

    public void Load()
    {
        _palette = new Palette(content);
        _kinds = new Kinds(content, _palette);
        _cart = new Cart(content, _palette);
        _pane = content.Mesh(Shapes.Quad(Vector2.One, _palette.Pane));
        Build(random.Seed);
        UseLighting(_lighting);
    }

    /// <summary>Switches the graphics settings to <paramref name="lighting"/>.</summary>
    private void UseLighting(Lighting lighting)
    {
        _lighting = lighting;
        graphics.BakedLighting = lighting == Lighting.Baked;
        graphics.LightProbes = lighting != Lighting.Direct;
    }

    public void Update(GameTime time)
    {
        if (input.Pressed(Key.D1))
        {
            UseLighting(Lighting.Live);
        }
        else if (input.Pressed(Key.D2))
        {
            UseLighting(Lighting.Baked);
        }
        else if (input.Pressed(Key.D3))
        {
            UseLighting(Lighting.Direct);
        }

        // The live light probes drawn over the scene, to watch the cart's light move between chunks.
        if (input.Pressed(Key.P))
        {
            graphics.DebugView = graphics.DebugView == DebugView.Probes ? DebugView.None : DebugView.Probes;
        }

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
        var buildings = _village.Buildings;
        for (int i = 0; i < buildings.Count; i++)
        {
            var building = buildings[i];
            DrawBuilding(view, building, seconds, elevation, inside: pose.Indoors > 0.5f && building.Index == _tour.Cottage.Index);
        }

        // The cart moves, so it stays out of the bakes and is lit by the probes like the rest of what moves.
        var cart = Cart.Placement(_village, seconds);
        _cart.Draw(view, CartKey, cart, seconds, _cartColor);
        float cartLamp = 1f - DayCycle.Smooth(-0.06f, 0.02f, elevation);
        if (cartLamp > 0.01f)
        {
            var lantern = Vector3.Transform(Cart.Lantern, cart);
            float flicker = Flicker(seconds, 7f);
            view.SpotLight(
                CartLightKey, lantern, -Vector3.UnitY, Palette.Lamplight * 3f * cartLamp * flicker, 8f, 0.6f, 1.35f, mode: LightMode.Realtime);
        }
    }

    /// <summary>The lighting modes in the bottom-left corner, with the current one in bold.</summary>
    public UiView? Ui
    {
        get
        {
            var modes = new VStack(HorizontalAlignment.Leading, 4f);
            foreach (var (lighting, key, name) in new[]
            {
                (Lighting.Live, 1, "Live lighting"),
                (Lighting.Baked, 2, "Baked lighting"),
                (Lighting.Direct, 3, "Direct light only"),
            })
            {
                var text = new Text($"{key}  {name}");
                modes.Add(lighting == _lighting ? text.Bold() : text.Foreground(Muted));
            }

            var probes = new Text("P  Light probes");
            modes.Add(graphics.DebugView == DebugView.Probes ? probes.Bold() : probes.Foreground(Muted));

            return modes
                .Padding(12f)
                .Background(Panel)
                .CornerRadius(8f)
                .Padding(20f)
                .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.BottomLeading);
        }
    }

    private void Build(int seed)
    {
        _village = Village.Generate(seed, _kinds);
        _ground = new Ground(content, _village);
        _tour = new CameraTour(_village);
        _cartColor = Palette.Accents[(seed % Palette.Accents.Length + Palette.Accents.Length) % Palette.Accents.Length];
    }

    /// <summary>
    /// Draws a building as a baked chunk, with its lamps and its lit windows. Its box is its
    /// plan's, placed where the village put it, so every copy of a plan shares one bake.
    /// <paramref name="inside"/> is true when the camera stands in it: the glowing panes are
    /// left out then, as a pane is seen from both sides and would hide the night outside.
    /// </summary>
    private void DrawBuilding(View view, Building building, float seconds, float elevation, bool inside)
    {
        int key = BuildingKey + building.Index * KeysPerBuilding;
        int lightKey = LightKey + building.Index * KeysPerBuilding;
        float level = 1f - DayCycle.Smooth(building.Dusk - 0.04f, building.Dusk + 0.04f, elevation);
        float flicker = Flicker(seconds, building.Phase);
        float fire = 1f + 0.18f * MathF.Sin(seconds * 9f + building.Phase) + 0.1f * MathF.Sin(seconds * 21f + building.Phase * 1.3f);
        var plan = building.Plan;
        view.BakedChunk(plan.Name, building.Placement, plan.Min, plan.Max, TexelsPerMetre, plan.EdgeBlend);

        for (int p = 0; p < plan.Parts.Length; p++)
        {
            var part = plan.Parts[p];
            Vector3? tint = part.Tint switch
            {
                PartTint.Roof => plan.RoofColor,
                PartTint.Accent => plan.AccentColor,
                PartTint.Lamps => new Vector3(1f + level * flicker),
                PartTint.Fire => new Vector3(1f + fire),
                _ => null,
            };

            view.Model(key + p, part.Model, building.Placement, tint, isStatic: !part.Glows);
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
                    lamp.OuterAngle,
                    mode: lamp.Mode);
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
                volumetric: 0.3f,
                mode: LightMode.Realtime);
        }
    }

    /// <summary>A lamp's flame wavering, about 1, different for every building.</summary>
    private static float Flicker(float seconds, float phase) =>
        1f + 0.07f * MathF.Sin(seconds * 11f + phase) + 0.05f * MathF.Sin(seconds * 23f + phase * 1.7f);
}
