using Microsoft.Extensions.DependencyInjection;
using Super.Physics;

namespace SkylarkExample;

public static class SkylarkServices
{
    /// <summary>Everything the game asks for. Program.cs and the tests both call this.</summary>
    public static IServiceCollection AddSkylark(this IServiceCollection services) => services
        .AddSuperPhysics()
        .AddSingleton<Look>()
        .AddSingleton<Levels>()
        .AddSingleton<Scenery>()
        .AddSingleton<Journey>()
        .AddSingleton<Chase>()
        .AddSingleton<Lifts>()
        .AddSingleton<Bird>()
        .AddSingleton<Gems>()
        .AddSingleton<Crates>();
}
