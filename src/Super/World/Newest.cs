using Rendition;

namespace Super;

/// <summary>
/// The order entities were given a component the frame shows only one of, such as a <see cref="Camera"/>, so the
/// newest wins and taking it away falls back to the one before. Each tracked type keeps the holders in the order they
/// got it; one that lost it or died is dropped when it's next looked up.
/// </summary>
internal sealed class Newest
{
    private readonly List<Entity>?[] _holders = new List<Entity>?[ComponentMask.MaxTypes];

    public Newest(ArchetypeTable types)
    {
        Track<Camera>(types);
        Track<Sky>(types);
        Track<Shadows>(types);
        Track<AmbientOcclusion>(types);
        Track<Finish>(types);
        Track<ScreenEffects>(types);
        Track<LensFlareSettings>(types);
        Track<PaintSettings>(types);
        Track<Outlines>(types);
        Track<ScreenBounce>(types);
        Track<LightProbes>(types);
        Track<BakedLighting>(types);
        Track<Underwater>(types);
        Track<Puddles>(types);
        Track<Wind>(types);
    }

    /// <summary>The entity that got a <typeparamref name="T"/> most recently and still has it, or null.</summary>
    public static Entity? Of<T>(World world)
        where T : struct
    {
        int type = world.Archetypes.TypeIdOrMissing<T>();
        return type < 0 ? null : world.Archetypes.Newest.Holder(world, type);
    }

    private Entity? Holder(World world, int type)
    {
        if (_holders[type] is not { } holders)
        {
            throw new InvalidOperationException($"Component type {type} isn't one the world keeps the newest of.");
        }

        for (int i = holders.Count - 1; i >= 0; i--)
        {
            var entity = holders[i];
            if (world.IsAlive(entity) && world.Has(entity, type))
            {
                return entity;
            }

            holders.RemoveAt(i);
        }

        return null;
    }

    private void Track<T>(ArchetypeTable types)
    {
        int type = types.TypeId<T>();
        var holders = _holders[type] = [];
        types.Reactions.Listen(type, entity =>
        {
            // One given it again moves to the end, so it's the newest once more.
            int earlier = holders.IndexOf(entity);
            if (earlier >= 0)
            {
                holders.RemoveAt(earlier);
            }

            holders.Add(entity);
        }, removed: false);
    }
}
