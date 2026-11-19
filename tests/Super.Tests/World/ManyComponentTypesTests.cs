using System.Reflection;

namespace Super.Tests;

public class ManyComponentTypesTests
{
    private record struct Numbered<T>(int Value);

    // Numbered<int>, Numbered<Numbered<int>>, ... gives as many distinct component types as asked for.
    private static Type TypeNumber(int depth)
    {
        var type = typeof(int);
        for (int i = 0; i <= depth; i++)
        {
            type = typeof(Numbered<>).MakeGenericType(type);
        }

        return type;
    }

    private static void AddNumbered(World world, Entity entity, int depth)
    {
        var type = TypeNumber(depth);
        var add = typeof(World).GetMethod(nameof(World.Add))!.MakeGenericMethod(type);
        add.Invoke(world, [entity, Activator.CreateInstance(type)]);
    }

    private static bool HasNumbered(World world, Entity entity, int depth)
    {
        var has = typeof(World).GetMethod(nameof(World.Has))!.MakeGenericMethod(TypeNumber(depth));
        return (bool)has.Invoke(world, [entity])!;
    }

    [Fact]
    public void Types_past_the_sixty_fourth_are_told_apart()
    {
        var world = new World();
        var filler = world.Create();
        for (int depth = 0; depth < 140; depth++)
        {
            AddNumbered(world, filler, depth);
        }

        var early = world.Create();
        AddNumbered(world, early, 6);
        var late = world.Create();
        AddNumbered(world, late, 70);

        Assert.False(HasNumbered(world, early, 70));
        Assert.True(HasNumbered(world, late, 70));
        Assert.False(HasNumbered(world, late, 6));
        Assert.True(HasNumbered(world, filler, 139));
    }

    [Fact]
    public void The_257th_component_type_is_refused()
    {
        var world = new World();
        var entity = world.Create();
        for (int depth = 0; depth < 256; depth++)
        {
            AddNumbered(world, entity, depth);
        }

        var error = Assert.Throws<TargetInvocationException>(() => AddNumbered(world, entity, 256));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal("A world holds at most 256 component types.", error.InnerException!.Message);
    }
}
