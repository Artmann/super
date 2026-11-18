using System.Numerics;
using Rendition;

namespace Super.Ecs;

public static class ViewExtensions
{
    private const int MaxParentDepth = 32;

    /// <summary>
    /// Draws every entity with a <see cref="Transform"/> and a <see cref="Model"/> or an
    /// <see cref="Animated"/>, keyed by entity ID. <see cref="Smoothed"/> entities blend from last tick, <see cref="Parent"/>
    /// entities follow their parent, <see cref="Swim"/> entities swim, and
    /// <see cref="Animated"/> entities take their animator's pose. Call it from Draw,
    /// alongside any models you describe by hand.
    /// </summary>
    public static void Models(this View view, World world)
    {
        var types = ModelTypes.Of(world);
        if (types.Transform < 0 || (types.Model < 0 && types.Animated < 0))
        {
            return;
        }

        world.BeginIteration();
        try
        {
            int index = -1;
            while (world.Archetypes.NextMatching(ref index, 1UL << types.Transform) is { } archetype)
            {
                var rows = ModelRows.Of(archetype, types);
                if (rows.Models is null && rows.Animated is null)
                {
                    continue;
                }

                for (int i = 0; i < archetype.Count; i++)
                {
                    rows.Draw(view, world, i);
                }
            }
        }
        finally
        {
            world.EndIteration();
        }
    }

    /// <summary>The component type IDs <see cref="Models"/> reads, or -1 for each the world has never seen.</summary>
    private readonly struct ModelTypes
    {
        public int Transform { get; init; }

        public int Model { get; init; }

        public int Animated { get; init; }

        public int Smoothed { get; init; }

        public int Parent { get; init; }

        public int Swim { get; init; }

        public int Static { get; init; }

        public static ModelTypes Of(World world) => new()
        {
            Transform = world.Archetypes.TypeIdOrMissing<Transform>(),
            Model = world.Archetypes.TypeIdOrMissing<Model>(),
            Animated = world.Archetypes.TypeIdOrMissing<Animated>(),
            Smoothed = world.Archetypes.TypeIdOrMissing<Smoothed>(),
            Parent = world.Archetypes.TypeIdOrMissing<Parent>(),
            Swim = world.Archetypes.TypeIdOrMissing<Swim>(),
            Static = world.Archetypes.TypeIdOrMissing<Static>(),
        };
    }

    /// <summary>One archetype's columns that <see cref="Models"/> draws from. A column the archetype lacks is null.</summary>
    private readonly struct ModelRows
    {
        public Entity[] Entities { get; init; }

        public Transform[] Transforms { get; init; }

        public Model[]? Models { get; init; }

        public Animated[]? Animated { get; init; }

        public Smoothed[]? Smoothed { get; init; }

        public Parent[]? Parents { get; init; }

        public Swim[]? Swims { get; init; }

        public bool IsStatic { get; init; }

        public static ModelRows Of(Archetype archetype, in ModelTypes types) => new()
        {
            Entities = archetype.Entities,
            Transforms = archetype.Column<Transform>(types.Transform).Values,
            Models = archetype.ColumnOrNull<Model>(types.Model)?.Values,
            Animated = archetype.ColumnOrNull<Animated>(types.Animated)?.Values,
            Smoothed = archetype.ColumnOrNull<Smoothed>(types.Smoothed)?.Values,
            Parents = archetype.ColumnOrNull<Parent>(types.Parent)?.Values,
            Swims = archetype.ColumnOrNull<Swim>(types.Swim)?.Values,
            IsStatic = types.Static >= 0 && (archetype.Mask & (1UL << types.Static)) != 0,
        };

        public void Draw(View view, World world, int i)
        {
            var animator = Animated?[i].Value;
            if (!TryGetModel(i, animator, out var model))
            {
                return;
            }

            float blend = view.Blend;
            var matrix = Smoothed is null ? Transforms[i].Matrix : Blended(in Transforms[i], in Smoothed[i], blend).Matrix;
            if (Parents is not null)
            {
                matrix *= WorldMatrix(world, Parents[i].Value, blend, depth: 0);
            }

            var swim = Swims is null ? default : SwimAt(i, blend);
            view.Model(Entities[i].Id, model.Value, matrix, model.Color, swim, animator?.Pose, model.Meshes, IsStatic, model.Fade);
        }

        /// <summary>A Model says what to draw; without one, an animator draws its rig's own model. False when neither is there.</summary>
        private bool TryGetModel(int i, Animator? animator, out Model model)
        {
            if (Models is not null)
            {
                model = Models[i];
                return true;
            }

            if (animator is null)
            {
                model = default;
                return false;
            }

            model = new Model(animator.Rig.Model ?? throw new InvalidOperationException(
                $"Entity {Entities[i].Id} is Animated with no Model, and its animator's rig wasn't read with content.Rig, so there's no model to draw. Add a Model."));
            return true;
        }

        private SwimMotion SwimAt(int i, float blend)
        {
            ref readonly var swim = ref Swims![i];
            float phase = Smoothed is not null && Smoothed[i].HasPrevious
                ? float.Lerp(Smoothed[i].PreviousPhase, swim.Phase, blend)
                : swim.Phase;
            return new SwimMotion(swim.Amplitude, swim.BodyLength, phase);
        }
    }

    internal static Transform Blended(in Transform current, in Smoothed smoothed, float blend) =>
        smoothed.HasPrevious ? Transform.Lerp(in smoothed.Previous, in current, blend) : current;

    /// <summary>An entity's matrix in the world, following its own parents. A dead parent, or one with no transform, counts as the world itself.</summary>
    internal static Matrix4x4 WorldMatrix(World world, Entity entity, float blend, int depth)
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
