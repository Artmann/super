using Microsoft.Extensions.DependencyInjection;

namespace Super.Physics;

public static class PhysicsServices
{
    /// <summary>
    /// Registers <see cref="Physics"/>, so classes can ask for one in their constructor. It reads colliders from
    /// the game's <see cref="World"/>, and is stepped by the tick's <see cref="GameTime.DeltaSeconds"/> before
    /// each Update.
    /// </summary>
    public static IServiceCollection AddSuperPhysics(this IServiceCollection services) =>
        services
            .AddSingleton<Physics>()
            .AddSingleton<ITickHook>(provider => provider.GetRequiredService<Physics>());
}
