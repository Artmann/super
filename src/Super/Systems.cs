using Microsoft.Extensions.DependencyInjection;

namespace Super;

/// <summary>One part of a game's update, such as moving enemies or spawning waves, run by <see cref="Systems"/>.</summary>
public interface ISystem
{
    /// <summary>Runs once a tick, when the game calls <see cref="Systems.Update"/>.</summary>
    void Update(GameTime time);
}

/// <summary>
/// Runs a game's systems in the order they were added, if you'd rather split <c>Update</c> up than write it in one
/// place. Ask for it in your game's constructor, add each system once, and call <see cref="Update"/> from the game's
/// <c>Update</c>: nothing runs on its own, so the game still says when.
/// </summary>
public sealed class Systems(IServiceProvider services)
{
    private readonly List<ISystem> _systems = [];

    /// <summary>
    /// Makes a <typeparamref name="T"/> and adds it after the others. It gets what its constructor asks for from the
    /// game's services, such as <see cref="World"/> or <see cref="Input"/>, without being registered itself.
    /// </summary>
    public Systems Add<T>()
        where T : class, ISystem
    {
        foreach (var system in _systems)
        {
            if (system is T)
            {
                throw new InvalidOperationException($"{typeof(T).Name} is added already.");
            }
        }

        _systems.Add(ActivatorUtilities.CreateInstance<T>(services));
        return this;
    }

    /// <summary>Runs every system once, in the order they were added.</summary>
    public void Update(GameTime time)
    {
        foreach (var system in _systems)
        {
            system.Update(time);
        }
    }
}
