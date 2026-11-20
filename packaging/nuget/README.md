# Super

A modern C# game engine you use as a library, with a 3D renderer built in. Super is
made so a coding agent can build, run, test and see a game with no editor and no
person at the mouse.

```csharp
using System.Numerics;
using Super;

Game.Run<Spinner>(new GameOptions { Title = "Spinner" });

class Spinner(World world) : IGame
{
    Entity cube;

    public void Load()
    {
        world.Spawn(new Camera(Position: new Vector3(0, 2, 5), Target: Vector3.Zero));
        world.Spawn(new Sun(Direction: new Vector3(1, 2, 1), Color: new Vector3(3f)));
        cube = world.Spawn(new Model(ModelRef.Cube));
    }

    public void Update(GameTime time)
    {
        ref var transform = ref world.Get<Transform>(cube);
        transform.Rotation *= Quaternion.CreateFromAxisAngle(Vector3.UnitY, time.TickSeconds);
    }
}
```

## Start a game

You need Windows and a GPU with Vulkan 1.3, or a Mac with Apple silicon, and the .NET 10 SDK.

```
dotnet new console -n MyGame
cd MyGame
dotnet add package Super
dotnet run -c Release
```

Put the code above in `Program.cs`. This one package holds the engine, the Rendition
renderer, the ECS, physics and the test helpers.

A `content` folder beside the project is cooked by `dotnet build`, with no extra setup.
Shaders in a `Shaders` folder are compiled too, which needs the Vulkan SDK
(`winget install KhronosGroup.VulkanSDK`), or on a Mac `brew install shaderc`.

Every game has the same command line, so you can see it without a person at the mouse:

```
dotnet run -c Release -- --seed 42 --ticks 600 --screenshot shot.png
```

## Documentation

The [documentation](https://github.com/Artmann/super/blob/main/docs/README.md) starts with
[Getting started](https://github.com/Artmann/super/blob/main/docs/getting-started.md).
Super is early and changes often.

Super is under the MIT license. `CREDITS.md` in this package lists the third-party
libraries and fonts it includes.
