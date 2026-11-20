using System.Numerics;
using System.Runtime.CompilerServices;
using Rendition;

namespace Super;

/// <summary>
/// What the world puts in a frame's lights and settings by itself: every <see cref="Sun"/>, <see cref="PointLight"/> and
/// <see cref="SpotLight"/>, and the newest of each frame setting, such as <see cref="Sky"/> or
/// <see cref="Wind"/>, each through the view call it stands for, so it's checked the same.
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

    /// <summary>Passes every shown <typeparamref name="T"/> in the world to its view call.</summary>
    private static void Each<T>(View view, World world)
        where T : struct => WorldFrame.EachShown<T, Lighter>(view, world);

    private readonly struct Lighter : WorldFrame.IShownVisitor
    {
        public void Visit<T>(View view, World world, Entity entity, ref T value)
            where T : struct => Light(view, world, entity, ref value);
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
            var matrix = WorldFrame.WorldMatrix(world, entity, view.Blend, view.Clock.PoseAhead);
            view.Lights.Point(entity.Id, point with { Position = Vector3.Transform(point.Position, matrix) });
        }
        else if (typeof(T) == typeof(SpotLight))
        {
            ref var spot = ref Unsafe.As<T, SpotLight>(ref value);
            var matrix = WorldFrame.WorldMatrix(world, entity, view.Blend, view.Clock.PoseAhead);
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
        LightSettings(view, world);
        SurroundingSettings(view, world);
        EffectSettings(view, world);
    }

    private static void LightSettings(View view, World world)
    {
        if (Newest<Shadows>(world) is { } shadows)
        {
            view.Lights.Shadows(shadows.Radius, shadows.Cascades, shadows.SunSize, shadows.Contact);
        }

        if (Newest<LensFlareSettings>(world) is { } flare)
        {
            view.Lights.LensFlare(flare);
        }

        if (Newest<AmbientOcclusion>(world) is { } occlusion)
        {
            view.Indirect.AmbientOcclusion(occlusion.Radius, occlusion.Strength);
        }

        if (Newest<ScreenBounce>(world) is { } bounce)
        {
            view.Indirect.ScreenBounce(bounce.Strength, bounce.Radius);
        }

        if (Newest<LightProbes>(world) is { } probes)
        {
            view.Indirect.LightProbes(probes.Min, probes.Max, probes.Spacing);
        }

        if (Newest<BakedLighting>(world) is { } baked)
        {
            view.Indirect.BakedLighting(baked.Name, baked.Min, baked.Max, baked.ProbeSpacing, baked.TexelsPerMetre);
            view.Indirect.BakedSpaceFromWorld = true;
        }
    }

    private static void SurroundingSettings(View view, World world)
    {
        if (Newest<Sky>(world) is { } sky)
        {
            view.Surroundings.Sky(sky.Settings);
        }

        if (Newest<Underwater>(world) is { } water)
        {
            view.Surroundings.Water(water.Settings);
        }

        if (Newest<Puddles>(world) is { } puddles)
        {
            view.Surroundings.Puddles(puddles.Level, puddles.Band);
        }

        if (Newest<Wind>(world) is { } wind)
        {
            view.Surroundings.Wind(wind.Direction, wind.Strength, wind.Gustiness);
        }
    }

    private static void EffectSettings(View view, World world)
    {
        if (Newest<Finish>(world) is { } finish)
        {
            view.Effects.Finish(finish.Settings);
        }

        // Laid over the finish's own screen effects, so a hit's flash keeps a level's paper.
        if (Newest<ScreenEffects>(world) is { } screen)
        {
            view.Effects.Screen(screen);
        }

        if (Newest<PaintSettings>(world) is { } paint)
        {
            view.Effects.Paint(paint);
        }

        if (Newest<Outlines>(world) is { } outlines)
        {
            view.Effects.Outlines(outlines.Color, outlines.Thickness, outlines.FadeStart, outlines.FadeEnd);
        }
    }

    /// <summary>The newest entity's <typeparamref name="T"/>, or null when no entity has one.</summary>
    private static T? Newest<T>(World world)
        where T : struct =>
        Super.Newest.Of<T>(world) is { } holder ? world.Get<T>(holder) : null;
}
