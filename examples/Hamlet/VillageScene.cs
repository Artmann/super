using System.Numerics;
using Rendition;
using Super;

namespace Hamlet;

/// <summary>
/// One village's entities: the ground, every building's parts, lamps and lit windows, and the cart. Each building is
/// lit as its plan's baked chunk, so its static parts are the chunk's contents and every copy of a plan shares one bake.
/// </summary>
internal sealed class VillageScene
{
    private readonly World _world;
    private readonly Content _content;
    private readonly Village _village;
    private readonly Ground _ground;
    private readonly List<Entity> _groundEntities = [];
    private readonly BuildingEntities[] _buildings;
    private readonly CartEntities _cart;

    /// <summary>Spawns <paramref name="village"/> as it is at <paramref name="seconds"/>.</summary>
    public VillageScene(World world, Content content, Village village, VillagePieces pieces, float seconds)
    {
        _world = world;
        _content = content;
        _village = village;
        _ground = new Ground(content, village);

        // The ground isn't static: it runs under the buildings, and a chunk's bake holds only the building.
        foreach (var model in _ground.Models)
        {
            _groundEntities.Add(world.Spawn(Transform.Identity, new Model(model)));
        }

        _buildings = [.. village.Buildings.Select(building => BuildingEntities.Spawn(world, building, pieces.Pane))];
        var accents = Palette.Accents;
        _cart = CartEntities.Spawn(world, pieces.Cart, village, seconds, accents[(village.Seed % accents.Length + accents.Length) % accents.Length]);
    }

    /// <summary>The entities of building <paramref name="index"/>'s parts, in its plan's part order.</summary>
    public IReadOnlyList<Entity> PartsOf(int index) => _buildings[index].Parts;

    /// <summary>
    /// Lights the lamps and windows for <paramref name="seconds"/> and rolls the cart on. <paramref name="inside"/> is
    /// the building the camera stands in, or -1: its glowing panes are left out, as a pane is seen from both sides and
    /// would hide the night outside.
    /// </summary>
    public void Update(float seconds, int inside)
    {
        float elevation = DayCycle.Elevation(seconds);
        foreach (var building in _buildings)
        {
            building.Light(_world, BuildingGlow.At(building.Building, seconds, elevation), building.Building.Index == inside);
        }

        _cart.Roll(_world, _village, seconds);
    }

    /// <summary>The village shown.</summary>
    public Village Village => _village;

    /// <summary>Takes the village's entities away, and unloads its ground's models, which no other village uses.</summary>
    public void Destroy()
    {
        foreach (var entity in _groundEntities)
        {
            _world.Destroy(entity);
        }

        _ground.Unload(_content);

        foreach (var building in _buildings)
        {
            building.Destroy(_world);
        }

        _cart.Destroy(_world);
    }

    /// <summary>A building's entities: a part for each of its plan's parts, a light for each lamp, and a pane and a light for each window.</summary>
    private sealed record BuildingEntities(Building Building, Entity[] Parts, Entity[] Lamps, Entity[] Panes, Entity[] WindowLights)
    {
        public static BuildingEntities Spawn(World world, Building building, ModelRef pane)
        {
            var plan = building.Plan;
            var at = Spawning.Of(building.Placement);
            var parts = new Entity[plan.Parts.Length];
            for (int p = 0; p < parts.Length; p++)
            {
                var part = plan.Parts[p];
                Vector3? tint = part.Tint switch
                {
                    PartTint.Roof => plan.RoofColor,
                    PartTint.Accent => plan.AccentColor,
                    _ => null,
                };

                // What glows changes colour as its lamps flicker, so it stays out of the bake.
                parts[p] = part.Glows ? world.Spawn(at, new Model(part.Model, tint)) : world.Spawn(at, new Model(part.Model, tint), new Static());
            }

            var lamps = new Entity[plan.Lamps.Length];
            for (int j = 0; j < lamps.Length; j++)
            {
                lamps[j] = world.Spawn(LampLight(building, plan.Lamps[j], 0f), new Hidden());
            }

            var panes = new Entity[plan.Windows.Length];
            var windowLights = new Entity[plan.Windows.Length];
            for (int w = 0; w < panes.Length; w++)
            {
                var window = plan.Windows[w];
                var middle = window.Middle - window.Outward * 0.09f;
                var frame = Matrix4x4.CreateScale(window.Size.X, window.Size.Y, 1f) * PlanBuilder.Facing(middle, window.Outward) * building.Placement;
                panes[w] = world.Spawn(Spawning.Of(frame), new Model(pane), new Hidden());
                windowLights[w] = world.Spawn(WindowLight(building, window, 0f), new Hidden());
            }

            return new BuildingEntities(building, parts, lamps, panes, windowLights);
        }

        /// <summary>Sets the lamps and the glowing parts and windows to <paramref name="glow"/>.</summary>
        public void Light(World world, BuildingGlow glow, bool inside)
        {
            var plan = Building.Plan;
            for (int p = 0; p < Parts.Length; p++)
            {
                var tint = plan.Parts[p].Tint;
                if (tint is PartTint.Lamps or PartTint.Fire)
                {
                    world.Get<Model>(Parts[p]).Color = new Vector3(tint == PartTint.Lamps ? 1f + glow.Level * glow.Flicker : 1f + glow.Fire);
                }
            }

            for (int j = 0; j < Lamps.Length; j++)
            {
                var lamp = plan.Lamps[j];
                float strength = lamp.Fire ? glow.Fire : glow.Level * glow.Flicker;
                world.Get<SpotLight>(Lamps[j]) = LampLight(Building, lamp, strength);
                Spawning.Show(world, Lamps[j], strength > 0.01f);
            }

            // Lit windows: a glowing pane in each, and a little warm light thrown out onto the ground.
            bool lit = glow.Level > 0.01f;
            for (int w = 0; w < Panes.Length; w++)
            {
                world.Get<Model>(Panes[w]).Color = new Vector3(1f + glow.Level * glow.Flicker);
                Spawning.Show(world, Panes[w], lit && !inside);
                world.Get<SpotLight>(WindowLights[w]) = WindowLight(Building, plan.Windows[w], glow.Level * glow.Flicker);
                Spawning.Show(world, WindowLights[w], lit);
            }
        }

        public void Destroy(World world)
        {
            foreach (var group in (ReadOnlySpan<Entity[]>)[Parts, Lamps, Panes, WindowLights])
            {
                foreach (var entity in group)
                {
                    world.Destroy(entity);
                }
            }
        }

        private static SpotLight LampLight(Building building, Lamp lamp, float strength) =>
            new(building.ToWorld(lamp.Position), building.DirectionToWorld(lamp.Direction), lamp.Color * strength, lamp.Range)
            {
                InnerAngle = lamp.InnerAngle,
                OuterAngle = lamp.OuterAngle,
                Mode = lamp.Mode,
            };

        private static SpotLight WindowLight(Building building, Window window, float strength) =>
            new(
                building.ToWorld(window.Middle + window.Outward * 0.25f),
                building.DirectionToWorld(window.Outward) + new Vector3(0f, -0.6f, 0f),
                Palette.Lamplight * 1.6f * strength,
                7f)
            {
                InnerAngle = 0.5f,
                OuterAngle = 1.2f,
                Shadows = false,
                Volumetric = 0.3f,
                Mode = LightMode.Realtime,
            };
    }
}

/// <summary>
/// How lit a building is now: its lamps and windows' level as dusk falls, their flame's flicker, and its fires' own
/// flicker.
/// </summary>
internal readonly record struct BuildingGlow(float Level, float Flicker, float Fire)
{
    public static BuildingGlow At(Building building, float seconds, float elevation) => new(
        1f - DayCycle.Smooth(building.Dusk - 0.04f, building.Dusk + 0.04f, elevation),
        Wavering(seconds, building.Phase),
        1f + 0.18f * MathF.Sin(seconds * 9f + building.Phase) + 0.1f * MathF.Sin(seconds * 21f + building.Phase * 1.3f));

    /// <summary>A lamp's flame wavering, about 1, different for every <paramref name="phase"/>.</summary>
    public static float Wavering(float seconds, float phase) =>
        1f + 0.07f * MathF.Sin(seconds * 11f + phase) + 0.05f * MathF.Sin(seconds * 23f + phase * 1.7f);
}

/// <summary>What every village is built with: the cart and the lit windows' pane.</summary>
internal sealed record VillagePieces(Cart Cart, ModelRef Pane);
