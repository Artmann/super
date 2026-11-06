using Microsoft.Extensions.DependencyInjection;
using Super.Ecs;

namespace Super.Physics;

public static class PhysicsServices
{
    /// <summary>
    /// Registers <see cref="Physics"/>, so classes can ask for one in their constructor, and the ECS's
    /// <see cref="World"/> it reads colliders from. The world is stepped by the tick's
    /// <see cref="GameTime.DeltaSeconds"/> before each Update.
    /// </summary>
    public static IServiceCollection AddSuperPhysics(this IServiceCollection services)
    {
        if (!services.Any(service => service.ServiceType == typeof(World)))
        {
            services.AddSuperEcs();
        }

        return services
            .AddSingleton<Physics>()
            .AddSingleton<ITickHook>(provider => provider.GetRequiredService<Physics>());
    }
}
