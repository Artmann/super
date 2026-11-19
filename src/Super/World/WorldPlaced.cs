using System.Numerics;
using System.Runtime.CompilerServices;

namespace Super;

/// <summary>
/// What the world puts in a frame's effects and probes by itself: every <see cref="Decal"/>, <see cref="FogVolume"/>,
/// <see cref="ReflectionCapture"/> and <see cref="BakedPiece"/>, placed by its entity and passed through the view call
/// it stands for, so it's checked the same. Hidden entities are left out.
/// </summary>
internal static class WorldPlaced
{
    /// <summary>Describes the world's placed things into a view that has just begun its frame.</summary>
    public static void Describe(View view, World world)
    {
        world.BeginIteration();
        try
        {
            Each<Decal>(view, world);
            Each<FogVolume>(view, world);
            Each<ReflectionCapture>(view, world);
            Each<BakedPiece>(view, world);
        }
        finally
        {
            world.EndIteration();
        }
    }

    /// <summary>Passes every shown <typeparamref name="T"/> in the world to its view call.</summary>
    private static void Each<T>(View view, World world)
        where T : struct => WorldFrame.EachShown<T, Placer>(view, world);

    private readonly struct Placer : WorldFrame.IShownVisitor
    {
        public void Visit<T>(View view, World world, Entity entity, ref T value)
            where T : struct => Place(view, entity, ref value, WorldFrame.WorldMatrix(world, entity, view.Blend, view.Clock.PoseAhead));
    }

    // T is a struct, so each branch but the one that matches is compiled away.
    private static void Place<T>(View view, Entity entity, ref T value, Matrix4x4 matrix)
        where T : struct
    {
        if (typeof(T) == typeof(Decal))
        {
            ref var decal = ref Unsafe.As<T, Decal>(ref value);
            view.Effects.Decal(entity.Id, matrix, decal.Material, decal.Blend);
        }
        else if (typeof(T) == typeof(FogVolume))
        {
            ref var fog = ref Unsafe.As<T, FogVolume>(ref value);
            view.Effects.FogVolume(entity.Id, fog.Shape, matrix, fog.Settings);
        }
        else if (typeof(T) == typeof(ReflectionCapture))
        {
            ref var capture = ref Unsafe.As<T, ReflectionCapture>(ref value);
            var position = matrix.Translation;
            view.Indirect.ReflectionProbe(entity.Id, position, position + capture.BoxMin, position + capture.BoxMax,
                capture.Blend, capture.Priority, capture.Update, capture.Version);
        }
        else if (typeof(T) == typeof(BakedPiece))
        {
            ref var piece = ref Unsafe.As<T, BakedPiece>(ref value);
            view.Indirect.BakedChunk(piece.Name, matrix, piece.Min, piece.Max, piece.TexelsPerMetre, piece.EdgeBlend);
        }
    }
}
