namespace Super;

/// <summary>Marks an entity as one a scene spawned, so it goes when the scene does.</summary>
internal readonly record struct SceneMember(int Scene);

/// <summary>
/// One built scene: what <see cref="Scenes.Go"/> hands its build function. Entities spawned through it belong to the
/// scene and are destroyed when the game goes to another, along with their children. Entities spawned straight on the
/// <see cref="World"/> belong to no scene and stay, such as a party or the music.
/// </summary>
public sealed class Scene
{
    internal Scene(World world, int id, Action<Scene> build)
    {
        World = world;
        Id = id;
        Build = build;
    }

    /// <summary>The world the scene's entities live in.</summary>
    public World World { get; }

    internal int Id { get; }

    internal Action<Scene> Build { get; }

    /// <summary>Makes an entity that belongs to the scene, like <see cref="WorldSpawn.Spawn{T1}"/>.</summary>
    public Entity Spawn<T1>(in T1 first)
        where T1 : struct =>
        new WorldSpawn.Spawning(World, Id).Fill(in first);

    /// <summary>Makes an entity that belongs to the scene, like <see cref="WorldSpawn.Spawn{T1, T2}"/>.</summary>
    public Entity Spawn<T1, T2>(in T1 first, in T2 second)
        where T1 : struct
        where T2 : struct =>
        new WorldSpawn.Spawning(World, Id).Fill(in first, in second);

    /// <summary>Makes an entity that belongs to the scene, like <see cref="WorldSpawn.Spawn{T1, T2, T3}"/>.</summary>
    public Entity Spawn<T1, T2, T3>(in T1 first, in T2 second, in T3 third)
        where T1 : struct
        where T2 : struct
        where T3 : struct =>
        new WorldSpawn.Spawning(World, Id).Fill(in first, in second, in third);

    /// <summary>Makes an entity that belongs to the scene, like <see cref="WorldSpawn.Spawn{T1, T2, T3, T4}"/>.</summary>
    public Entity Spawn<T1, T2, T3, T4>(in T1 first, in T2 second, in T3 third, in T4 fourth)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct =>
        new WorldSpawn.Spawning(World, Id).Fill(in first, in second, in third, in fourth);

    /// <summary>Makes an entity that belongs to the scene, like <see cref="WorldSpawn.Spawn{T1, T2, T3, T4, T5}"/>.</summary>
    public Entity Spawn<T1, T2, T3, T4, T5>(in T1 first, in T2 second, in T3 third, in T4 fourth, in T5 fifth)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
        where T5 : struct =>
        new WorldSpawn.Spawning(World, Id).Fill(in first, in second, in third, in fourth, in fifth);
}
