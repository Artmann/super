using Microsoft.Extensions.DependencyInjection;

namespace Super.Ecs;

public static class EcsServices
{
    /// <summary>Registers a <see cref="World"/>, so classes can ask for one in their constructor.</summary>
    public static IServiceCollection AddSuperEcs(this IServiceCollection services) =>
        services
            .AddSingleton<World>()
            .AddSingleton<ITickHook>(provider => provider.GetRequiredService<World>())
            .AddSingleton<IStateHook>(provider => provider.GetRequiredService<World>());
}
