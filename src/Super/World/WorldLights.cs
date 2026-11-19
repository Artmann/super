using System.Numerics;
using System.Runtime.CompilerServices;

namespace Super;

/// <summary>
/// What the world puts in a frame's lights and settings by itself: every <see cref="Sun"/>, <see cref="PointLight"/> and
/// <see cref="SpotLight"/>, and the newest <see cref="Sky"/>, <see cref="Shadows"/>, <see cref="AmbientOcclusion"/>,
/// <see cref="Finish"/> and <see cref="Outlines"/>, each through the view call it stands for, so it's checked the same.
/// </summary>
internal static class WorldLights
{
    /// <summary>Describes the world's lights and settings into a view that has just begun its frame.</summary>
    public static void Describe(View view, World world)
    {
        world.BeginIteration();
        try
        {
            Each<Sun>(view, world);
            Each<PointLight>(view, world);
            Each<SpotLight>(view, world);
        }
        finally
        {
            world.EndIteration();
        }

        Settings(view, world);
    }

    /// <summary>Passes every <typeparamref name="T"/> in the world to its view call.</summary>
    private static void Each<T>(View view, World world)
        where T : struct
    {
        int type = world.Archetypes.TypeIdOrMissing<T>();
        if (type < 0)
        {
            return;
        }

        int index = -1;
        while (world.Archetypes.NextMatching(ref index, default(ComponentMask).With(type)) is { } archetype)
        {
            var values = archetype.Column<T>(type).Values;
            for (int i = 0; i < archetype.Count; i++)
            {
                Light(view, world, archetype.Entities[i], ref values[i]);
            }
        }
    }

    // T is a struct, so each branch but the one that matches is compiled away.
    private static void Light<T>(View view, World world, Entity entity, ref T value)
        where T : struct
    {
        if (typeof(T) == typeof(Sun))
        {
            ref var sun = ref Unsafe.As<T, Sun>(ref value);
            view.Lights.Sun(sun.Direction, sun.Color, sun.CastsShadows, sun.Mode);
        }
        else if (typeof(T) == typeof(PointLight))
        {
            ref var point = ref Unsafe.As<T, PointLight>(ref value);
            var matrix = WorldFrame.WorldMatrix(world, entity, view.Blend);
            view.Lights.Point(entity.Id, point with { Position = Vector3.Transform(point.Position, matrix) });
        }
        else if (typeof(T) == typeof(SpotLight))
        {
            ref var spot = ref Unsafe.As<T, SpotLight>(ref value);
            var matrix = WorldFrame.WorldMatrix(world, entity, view.Blend);
            var direction = spot.Direction == Vector3.Zero ? Vector3.UnitZ : spot.Direction;
            view.Lights.Spot(entity.Id, spot with
            {
                Position = Vector3.Transform(spot.Position, matrix),
                Direction = Vector3.TransformNormal(direction, matrix),
            });
        }
    }

    /// <summary>The newest of each setting, through its view call. A setting no entity has leaves the view's own.</summary>
    private static void Settings(View view, World world)
    {
        if (Newest.Of<Sky>(world) is { } sky)
        {
            view.Surroundings.Sky(world.Get<Sky>(sky).Settings);
        }

        if (Newest.Of<Shadows>(world) is { } shadowed)
        {
            var shadows = world.Get<Shadows>(shadowed);
            view.Lights.Shadows(shadows.Radius, shadows.Cascades, shadows.SunSize, shadows.Contact);
        }

        if (Newest.Of<AmbientOcclusion>(world) is { } occluded)
        {
            var occlusion = world.Get<AmbientOcclusion>(occluded);
            view.Indirect.AmbientOcclusion(occlusion.Radius, occlusion.Strength);
        }

        if (Newest.Of<Finish>(world) is { } finished)
        {
            view.Effects.Finish(world.Get<Finish>(finished).Settings);
        }

        if (Newest.Of<Outlines>(world) is { } outlined)
        {
            var outlines = world.Get<Outlines>(outlined);
            view.Effects.Outlines(outlines.Color, outlines.Thickness, outlines.FadeStart, outlines.FadeEnd);
        }
    }
}
