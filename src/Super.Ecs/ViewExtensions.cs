using System.Numerics;
using Rendition;

namespace Super.Ecs;

public static class ViewExtensions
{
    private const int MaxParentDepth = 32;

    /// <summary>
    /// Draws every entity with a <see cref="Transform"/> and a <see cref="Model"/>, keyed
    /// by entity ID. <see cref="Smoothed"/> entities blend from last tick, <see cref="Parent"/>
    /// entities follow their parent, and <see cref="Swim"/> entities swim. Call it from
    /// Draw, alongside any models you describe by hand.
    /// </summary>
    public static void Models(this View view, World world)
    {
        int transformType = world.TypeIdOrMissing<Transform>();
        int modelType = world.TypeIdOrMissing<Model>();
        if (transformType < 0 || modelType < 0)
        {
            return;
        }

        int smoothedType = world.TypeIdOrMissing<Smoothed>();
        int parentType = world.TypeIdOrMissing<Parent>();
        int swimType = world.TypeIdOrMissing<Swim>();
        ulong mask = (1UL << transformType) | (1UL << modelType);
        float blend = view.Blend;

        world.BeginIteration();
        try
        {
            int index = -1;
            while (world.NextArchetype(ref index, mask) is { } archetype)
            {
                var entities = archetype.Entities;
                var transforms = archetype.Column<Transform>(transformType).Values;
                var models = archetype.Column<Model>(modelType).Values;
                var smoothed = archetype.ColumnOrNull<Smoothed>(smoothedType)?.Values;
                var parents = archetype.ColumnOrNull<Parent>(parentType)?.Values;
                var swims = archetype.ColumnOrNull<Swim>(swimType)?.Values;

                for (int i = 0; i < archetype.Count; i++)
                {
                    var matrix = smoothed is null ? transforms[i].Matrix : Blended(in transforms[i], in smoothed[i], blend).Matrix;
                    if (parents is not null)
                    {
                        matrix *= WorldMatrix(world, parents[i].Value, blend, depth: 0);
                    }

                    ref readonly var model = ref models[i];
                    if (swims is null)
                    {
                        view.Model(entities[i].Id, model.Value, matrix, model.Color);
                        continue;
                    }

                    ref readonly var swim = ref swims[i];
                    float phase = smoothed is not null && smoothed[i].HasPrevious
                        ? float.Lerp(smoothed[i].PreviousPhase, swim.Phase, blend)
                        : swim.Phase;
                    view.Model(entities[i].Id, model.Value, matrix, model.Color, new SwimMotion(swim.Amplitude, swim.BodyLength, phase));
                }
            }
        }
        finally
        {
            world.EndIteration();
        }
    }

    private static Transform Blended(in Transform current, in Smoothed smoothed, float blend) =>
        smoothed.HasPrevious ? Transform.Lerp(in smoothed.Previous, in current, blend) : current;

    /// <summary>An entity's matrix in the world, following its own parents. A dead parent, or one with no transform, counts as the world itself.</summary>
    private static Matrix4x4 WorldMatrix(World world, Entity entity, float blend, int depth)
    {
        if (depth >= MaxParentDepth || !world.IsAlive(entity) || !world.Has<Transform>(entity))
        {
            return Matrix4x4.Identity;
        }

        ref readonly var transform = ref world.Get<Transform>(entity);
        var matrix = world.Has<Smoothed>(entity) ? Blended(in transform, in world.Get<Smoothed>(entity), blend).Matrix : transform.Matrix;
        if (world.Has<Parent>(entity))
        {
            matrix *= WorldMatrix(world, world.Get<Parent>(entity).Value, blend, depth + 1);
        }

        return matrix;
    }
}
