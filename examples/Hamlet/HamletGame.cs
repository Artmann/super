using System.Numerics;
using Examples;
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
    /// <summary>The lightmap density of every building's bake.</summary>
    internal const int TexelsPerMetre = 10;

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

    private readonly LightingSwitch _lighting = new(input, graphics);
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

    /// <summary>The village now shown.</summary>
    internal Village Village => _village;

    /// <summary>The lighting now on.</summary>
    internal LightingMode CurrentLighting => _lighting.Current;

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
        var palette = new Palette(content);
        _kinds = new Kinds(content, palette);
        _cart = new Cart(content, palette);
        _pane = content.Mesh(Shapes.Quad(Vector2.One, palette.Pane));
        Build(random.Seed);
        _lighting.Use(_lighting.Current);
    }

    public void Update(GameTime time)
    {
        _lighting.Update();

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
            view.Lights.Shadows(18f, 2);
            view.Indirect.AmbientOcclusion(1.5f, 1f);
        }
        else
        {
            view.Lights.Shadows(90f, 3);
        }

        for (int i = 0; i < _ground.Models.Count; i++)
        {
            if (content.Models.IsReady(_ground.Models[i]))
            {
                view.Model(GroundKey + i, _ground.Models[i], Matrix4x4.Identity);
            }
        }

        float elevation = DayCycle.Elevation(seconds);
        var buildings = _village.Buildings;
        for (int i = 0; i < buildings.Count; i++)
        {
            var building = buildings[i];
            BuildingDrawing.Draw(view, building, _pane, BuildingGlow.At(building, seconds, elevation), inside: pose.Indoors > 0.5f && building.Index == _tour.Cottage.Index);
        }

        DrawCart(view, seconds, elevation);
    }

    /// <summary>The lighting modes in the bottom-left corner, with the current one in bold, and whether P shows the probes.</summary>
    public UiView? Ui => _lighting.Panel(modes =>
    {
        var probes = new Text("P  Light probes");
        modes.Add(graphics.DebugView == DebugView.Probes ? probes.Bold() : probes.Foreground(LightingSwitch.Muted));
    });

    /// <summary>
    /// The cart, with its lantern lit at dusk. The cart moves, so it stays out of the bakes and is lit by the probes
    /// like the rest of what moves.
    /// </summary>
    private void DrawCart(View view, float seconds, float elevation)
    {
        var cart = Cart.Placement(_village, seconds);
        _cart.Draw(view, CartKey, cart, seconds, _cartColor);
        float cartLamp = 1f - DayCycle.Smooth(-0.06f, 0.02f, elevation);
        if (cartLamp > 0.01f)
        {
            var lantern = Vector3.Transform(Cart.Lantern, cart);
            float flicker = BuildingGlow.Wavering(seconds, 7f);
            view.Lights.Spot(
                CartLightKey, new SpotLight(lantern, -Vector3.UnitY, Palette.Lamplight * 3f * cartLamp * flicker, 8f) { InnerAngle = 0.6f, OuterAngle = 1.35f, Mode = LightMode.Realtime });
        }
    }

    private void Build(int seed)
    {
        _village = Village.Generate(seed, _kinds);
        _ground = new Ground(content, _village);
        _tour = new CameraTour(_village);
        _cartColor = Palette.Accents[(seed % Palette.Accents.Length + Palette.Accents.Length) % Palette.Accents.Length];
    }

    /// <summary>Draws the village's buildings: each as a baked chunk, with its lamps and its lit windows.</summary>
    private static class BuildingDrawing
    {
        /// <summary>
        /// Draws a building. Its box is its plan's, placed where the village put it, so every copy of a plan shares
        /// one bake. <paramref name="inside"/> is true when the camera stands in it: the glowing panes are left out
        /// then, as a pane is seen from both sides and would hide the night outside.
        /// </summary>
        public static void Draw(View view, Building building, ModelRef pane, BuildingGlow glow, bool inside)
        {
            var plan = building.Plan;
            view.Indirect.BakedChunk(plan.Name, building.Placement, plan.Min, plan.Max, TexelsPerMetre, plan.EdgeBlend);

            DrawParts(view, building, glow);
            DrawLamps(view, building, glow);
            if (glow.Level > 0.01f)
            {
                DrawWindows(view, building, pane, glow, inside);
            }
        }

        private static void DrawParts(View view, Building building, BuildingGlow glow)
        {
            int key = BuildingKey + building.Index * KeysPerBuilding;
            var plan = building.Plan;
            for (int p = 0; p < plan.Parts.Length; p++)
            {
                var part = plan.Parts[p];
                Vector3? tint = part.Tint switch
                {
                    PartTint.Roof => plan.RoofColor,
                    PartTint.Accent => plan.AccentColor,
                    PartTint.Lamps => new Vector3(1f + glow.Level * glow.Flicker),
                    PartTint.Fire => new Vector3(1f + glow.Fire),
                    _ => null,
                };

                view.Model(key + p, part.Model, building.Placement, new ModelOptions { Color = tint, Static = !part.Glows });
            }
        }

        private static void DrawLamps(View view, Building building, BuildingGlow glow)
        {
            int lightKey = LightKey + building.Index * KeysPerBuilding;
            var lamps = building.Plan.Lamps;
            for (int j = 0; j < lamps.Length; j++)
            {
                var lamp = lamps[j];
                float strength = lamp.Fire ? glow.Fire : glow.Level * glow.Flicker;
                if (strength > 0.01f)
                {
                    view.Lights.Spot(
                        lightKey + j,
                        new SpotLight(
                            building.ToWorld(lamp.Position),
                            building.DirectionToWorld(lamp.Direction),
                            lamp.Color * strength,
                            lamp.Range)
                        {
                            InnerAngle = lamp.InnerAngle,
                            OuterAngle = lamp.OuterAngle,
                            Mode = lamp.Mode,
                        });
                }
            }
        }

        /// <summary>Lit windows: a glowing pane in each, and a little warm light thrown out onto the ground.</summary>
        private static void DrawWindows(View view, Building building, ModelRef pane, BuildingGlow glow, bool inside)
        {
            int key = BuildingKey + building.Index * KeysPerBuilding;
            int lightKey = LightKey + building.Index * KeysPerBuilding;
            var windows = building.Plan.Windows;
            for (int w = 0; w < windows.Length; w++)
            {
                var window = windows[w];
                var middle = window.Middle - window.Outward * 0.09f;
                var frame = Matrix4x4.CreateScale(window.Size.X, window.Size.Y, 1f) * PlanBuilder.Facing(middle, window.Outward) * building.Placement;
                if (!inside)
                {
                    view.Model(key + PaneKeys + w, pane, frame, new Vector3(1f + glow.Level * glow.Flicker));
                }

                var outward = building.DirectionToWorld(window.Outward);
                view.Lights.Spot(
                    lightKey + PaneKeys + w,
                    new SpotLight(
                        building.ToWorld(window.Middle + window.Outward * 0.25f),
                        outward + new Vector3(0f, -0.6f, 0f),
                        Palette.Lamplight * 1.6f * glow.Level * glow.Flicker,
                        7f)
                    {
                        InnerAngle = 0.5f,
                        OuterAngle = 1.2f,
                        Shadows = false,
                        Volumetric = 0.3f,
                        Mode = LightMode.Realtime,
                    });
            }
        }
    }

    /// <summary>
    /// How lit a building is now: its lamps and windows' level as dusk falls, their flame's flicker, and its fires'
    /// own flicker.
    /// </summary>
    private readonly record struct BuildingGlow(float Level, float Flicker, float Fire)
    {
        public static BuildingGlow At(Building building, float seconds, float elevation) => new(
            1f - DayCycle.Smooth(building.Dusk - 0.04f, building.Dusk + 0.04f, elevation),
            Wavering(seconds, building.Phase),
            1f + 0.18f * MathF.Sin(seconds * 9f + building.Phase) + 0.1f * MathF.Sin(seconds * 21f + building.Phase * 1.3f));

        /// <summary>A lamp's flame wavering, about 1, different for every <paramref name="phase"/>.</summary>
        public static float Wavering(float seconds, float phase) =>
            1f + 0.07f * MathF.Sin(seconds * 11f + phase) + 0.05f * MathF.Sin(seconds * 23f + phase * 1.7f);
    }
}
