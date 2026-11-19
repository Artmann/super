using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// What the world puts in a frame by itself, before the game's Draw: every entity's model, camera,
/// lights, settings, decals, fog volumes, probes and emitters, so a game describes the world once, as entities,
/// and Draw is only for what one frame shows.
/// </summary>
internal static class WorldFrame
{
    private const int MaxParentDepth = 32;

    // The key the static entities' draws are kept under, in the view's memory.
    private const int StaticKey = 0;

    // Kept in a field so drawing the static entities again makes no new delegate.
    private static readonly Action<ModelPass, View> DrawStatic = static (pass, view) => DrawArchetypes(view, pass);

    /// <summary>Describes everything the world shows into a view that has just begun its frame.</summary>
    public static void Describe(View view, World world)
    {
        if (WorldCameras.FrameCamera(world, view.Blend, view.Clock.PoseAhead) is { } camera)
        {
            view.Camera(camera);
        }

        WorldLights.Describe(view, world);
        WorldPlaced.Describe(view, world);
        Models(view, world);
        WorldEmitters.Describe(view, world);
    }

    /// <summary>
    /// Draws every entity with a <see cref="Transform"/> and a <see cref="Model"/> or an <see cref="Animated"/>, keyed by
    /// entity ID. Entities blend from where they were at the last tick, unless <see cref="Static"/>; <see cref="Parent"/>
    /// entities follow their parent, <see cref="Swim"/> entities swim, and <see cref="Animated"/> entities take their
    /// animator's pose. Static entities are drawn once and replayed, like <see cref="View.Cached"/>, until
    /// <see cref="ArchetypeTable.StaticVersion"/> says one changed.
    /// </summary>
    private static void Models(View view, World world)
    {
        var types = ModelTypes.Of(world);
        if (types.Transform < 0 || (types.Model < 0 && types.Animated < 0))
        {
            return;
        }

        world.BeginIteration();
        try
        {
            if (types.Static >= 0)
            {
                view.Cached(StaticKey, world.Archetypes.StaticVersion, new ModelPass(world, types, IsStatic: true), DrawStatic);
            }

            DrawArchetypes(view, new ModelPass(world, types, IsStatic: false));
        }
        finally
        {
            world.EndIteration();
        }
    }

    /// <summary>Draws every archetype with a transform whose static-ness matches the pass.</summary>
    private static void DrawArchetypes(View view, in ModelPass pass)
    {
        var types = pass.Types;
        int index = -1;
        while (pass.World.Archetypes.NextMatching(ref index, default(ComponentMask).With(types.Transform)) is { } archetype)
        {
            var rows = ModelRows.Of(archetype, types);
            bool hidden = types.Hidden >= 0 && archetype.Mask.Has(types.Hidden);
            if (hidden || rows.IsStatic != pass.IsStatic || (rows.Models is null && rows.Animated is null))
            {
                continue;
            }

            for (int i = 0; i < archetype.Count; i++)
            {
                rows.Draw(view, pass.World, i);
            }
        }
    }

    /// <summary>One walk over the world's archetypes: the static ones, or the rest.</summary>
    private readonly record struct ModelPass(World World, ModelTypes Types, bool IsStatic);

    /// <summary>The component type IDs <see cref="Models"/> reads, or -1 for each the world has never seen.</summary>
    private readonly struct ModelTypes
    {
        public int Transform { get; init; }

        public int Model { get; init; }

        public int Animated { get; init; }

        public int LastTick { get; init; }

        public int Parent { get; init; }

        public int Swim { get; init; }

        public int Static { get; init; }

        public int Ghost { get; init; }

        public int Hidden { get; init; }

        public static ModelTypes Of(World world) => new()
        {
            Transform = world.Archetypes.TypeIdOrMissing<Transform>(),
            Model = world.Archetypes.TypeIdOrMissing<Model>(),
            Animated = world.Archetypes.TypeIdOrMissing<Animated>(),
            LastTick = world.Archetypes.TypeIdOrMissing<LastTick>(),
            Parent = world.Archetypes.TypeIdOrMissing<Parent>(),
            Swim = world.Archetypes.TypeIdOrMissing<Swim>(),
            Static = world.Archetypes.TypeIdOrMissing<Static>(),
            Ghost = world.Archetypes.TypeIdOrMissing<Ghost>(),
            Hidden = world.Archetypes.TypeIdOrMissing<Hidden>(),
        };
    }

    /// <summary>One archetype's columns that <see cref="Models"/> draws from. A column the archetype lacks is null.</summary>
    private readonly struct ModelRows
    {
        public Entity[] Entities { get; init; }

        public Transform[] Transforms { get; init; }

        public Model[]? Models { get; init; }

        public Animated[]? Animated { get; init; }

        public LastTick[]? LastTicks { get; init; }

        public Parent[]? Parents { get; init; }

        public Swim[]? Swims { get; init; }

        public Ghost[]? Ghosts { get; init; }

        public bool IsStatic { get; init; }

        public static ModelRows Of(Archetype archetype, in ModelTypes types)
        {
            // A static entity is drawn where it is, even one that moved before it was made static.
            bool isStatic = types.Static >= 0 && archetype.Mask.Has(types.Static);
            return new ModelRows
            {
                Entities = archetype.Entities,
                Transforms = archetype.Column<Transform>(types.Transform).Values,
                Models = archetype.ColumnOrNull<Model>(types.Model)?.Values,
                Animated = archetype.ColumnOrNull<Animated>(types.Animated)?.Values,
                LastTicks = isStatic ? null : archetype.ColumnOrNull<LastTick>(types.LastTick)?.Values,
                Parents = archetype.ColumnOrNull<Parent>(types.Parent)?.Values,
                Swims = archetype.ColumnOrNull<Swim>(types.Swim)?.Values,
                Ghosts = archetype.ColumnOrNull<Ghost>(types.Ghost)?.Values,
                IsStatic = isStatic,
            };
        }

        public void Draw(View view, World world, int i)
        {
            var animator = Animated?[i].Value;
            if (!TryGetModel(i, animator, out var model) || (Parents is not null && IsHidden(world, Parents[i].Value)))
            {
                return;
            }

            float blend = view.Blend;
            var matrix = LastTicks is null ? Transforms[i].Matrix : Blended(in Transforms[i], in LastTicks[i], blend).Matrix;
            if (Parents is not null)
            {
                matrix *= ParentMatrix(world, Entities[i], Parents[i], blend, view.Clock.PoseAhead);
            }

            if (Ghosts is not null)
            {
                view.Model(Entities[i].Id, model.Value, matrix, Ghosts[i], model.Meshes);
                return;
            }

            var swim = Swims is null ? default : SwimAt(i, blend);
            view.Model(Entities[i].Id, model.Value, matrix, animator?.Pose, new ModelOptions
            {
                Color = model.Color,
                Swim = swim,
                Meshes = model.Meshes,
                Static = IsStatic,
                Fade = model.Fade,
            });
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
            float phase = LastTicks is not null && LastTicks[i].HasPrevious
                ? float.Lerp(LastTicks[i].PreviousPhase, swim.Phase, blend)
                : swim.Phase;
            return new SwimMotion(swim.Amplitude, swim.BodyLength, phase);
        }
    }

    /// <summary>What <see cref="EachShown"/> does with each entity, as a struct so the call is made without a delegate.</summary>
    internal interface IShownVisitor
    {
        void Visit<T>(View view, World world, Entity entity, ref T value)
            where T : struct;
    }

    /// <summary>Hands every <typeparamref name="T"/> in the world whose entity isn't <see cref="IsHidden"/> to the visitor.</summary>
    internal static void EachShown<T, TVisitor>(View view, World world)
        where T : struct
        where TVisitor : struct, IShownVisitor
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
                var entity = archetype.Entities[i];
                if (!IsHidden(world, entity))
                {
                    default(TVisitor).Visit(view, world, entity, ref values[i]);
                }
            }
        }
    }

    internal static Transform Blended(in Transform current, in LastTick last, float blend) =>
        last.HasPrevious ? Transform.Lerp(in last.Previous, in current, blend) : current;

    /// <summary>
    /// Whether an entity is left out of the frame and the audio: it has <see cref="Hidden"/>, or one of its parents
    /// does. A dead entity, or a chain deeper than any real one, isn't hidden.
    /// </summary>
    internal static bool IsHidden(World world, Entity entity)
    {
        int hidden = world.Archetypes.TypeIdOrMissing<Hidden>();
        if (hidden < 0)
        {
            return false;
        }

        for (int depth = 0; depth < MaxParentDepth && world.IsAlive(entity); depth++)
        {
            if (world.Has(entity, hidden))
            {
                return true;
            }

            if (!world.Has<Parent>(entity))
            {
                return false;
            }

            entity = world.ParentOf(entity).Value;
        }

        return false;
    }

    /// <summary>
    /// An entity's matrix in the world, following its own parents. A dead parent, or one with no transform, counts as the
    /// world itself. A parent's bone is placed <paramref name="poseAhead"/> seconds past the last tick, as the skinned
    /// model is drawn, or as of the tick when that's null, as physics needs.
    /// </summary>
    internal static Matrix4x4 WorldMatrix(World world, Entity entity, float blend, float? poseAhead = null) =>
        WorldMatrix(world, entity, blend, poseAhead, depth: 0);

    private static Matrix4x4 WorldMatrix(World world, Entity entity, float blend, float? poseAhead, int depth)
    {
        if (depth >= MaxParentDepth || !world.IsAlive(entity) || !world.Has<Transform>(entity))
        {
            return Matrix4x4.Identity;
        }

        ref readonly var transform = ref world.Get<Transform>(entity);
        bool blends = world.Has<LastTick>(entity) && !world.Has<Static>(entity);
        var matrix = blends ? Blended(in transform, in world.Get<LastTick>(entity), blend).Matrix : transform.Matrix;
        if (world.Has<Parent>(entity))
        {
            var parent = world.ParentOf(entity);
            matrix *= OnBone(world, entity, parent, poseAhead, WorldMatrix(world, parent.Value, blend, poseAhead, depth + 1));
        }

        return matrix;
    }

    /// <summary>What a child's own matrix counts from: its parent's matrix in the world, and the bone's place in the parent's model when it names one.</summary>
    internal static Matrix4x4 ParentMatrix(World world, Entity child, Parent parent, float blend, float? poseAhead) =>
        OnBone(world, child, parent, poseAhead, WorldMatrix(world, parent.Value, blend, poseAhead, depth: 0));

    private static Matrix4x4 OnBone(World world, Entity child, Parent parent, float? poseAhead, Matrix4x4 parentMatrix) =>
        parent.Bone is null || !world.IsAlive(parent.Value) ? parentMatrix : BoneMatrix(world, child, parent, poseAhead) * parentMatrix;

    private static Matrix4x4 BoneMatrix(World world, Entity child, Parent parent, float? poseAhead)
    {
        var animator = world.Has<Animated>(parent.Value) ? world.Get<Animated>(parent.Value).Value : null;
        if (animator is null)
        {
            throw new InvalidOperationException(
                $"Entity {child.Id}'s Parent names the bone {parent.Bone}, but its parent, entity {parent.Value.Id}, isn't Animated, so it has no bones.");
        }

        var skeleton = animator.Rig.Skeleton;
        int joint = skeleton.IndexOf(parent.Bone!);
        if (joint < 0)
        {
            throw new ArgumentException(
                $"Entity {child.Id}'s Parent names the bone {parent.Bone}, but entity {parent.Value.Id}'s rig has no joint called that. It has: {string.Join(", ", skeleton.Names)}.",
                nameof(parent));
        }

        return animator.Joints.At(joint, poseAhead);
    }
}
