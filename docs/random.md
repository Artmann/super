# Random numbers

`GameRandom` is the only source of randomness your game needs. It takes one seed and
gives you named streams:

```csharp
class Weather(GameRandom random)
{
    readonly RandomStream rolls = random.Stream("weather");

    public bool StormToday() => rolls.Chance(0.05f);
}
```

Each stream has its own sequence, so adding a dice roll to combat won't change the
weather. To replay a game exactly, use the same seed: set `GameOptions.Seed`, pass
`--seed` on the command line, or give one to `HeadlessGame.Create` in a test.

## What a stream gives

| Method | What you get |
| --- | --- |
| `Between(1, 6)` | A whole number, including both ends, so this is a die |
| `Between(0.5f, 2f)` | A decimal number in that range |
| `Float()` | A number from 0 up to, but not including, 1 |
| `Chance(0.05f)` | True 5% of the time |
| `Bool()`, `Sign()` | A coin flip, or either -1 or 1 |
| `Pick(items)`, `Shuffle(items)` | A random item, or a random order |
| `PickWeighted(items, item => item.Weight)` | A random item, where bigger weights come up more |
| `Normal(mean, deviation)` | A bell curve, for values that vary but rarely go wild |
| `Angle()`, `Direction2D()`, `Direction3D()` | A random way to face |
| `InsideCircle(radius)`, `InsideSphere(radius)` | A random point in that shape |

```csharp
var loot = rolls.PickWeighted(table.Drops, drop => drop.Weight);
var offset = rolls.InsideCircle(8f);
var spawn = home + new Vector3(offset.X, 0f, offset.Y);
var height = rolls.Normal(mean: 1.75f, deviation: 0.08f);
```

## Rules that keep a seed repeatable

- Take randomness from `GameRandom`, never `System.Random` or `Random.Shared`.
- Take time from `GameTime`, never the system clock.
- Give each system its own stream, named for what it does.

The generator is xoshiro256\*\*, and stream names are hashed with FNV-1a, so a name gives
the same stream on every run. Runs repeat exactly on the same build and the same kind of
CPU; different CPU types can round decimal numbers differently.

`Super.Random` is its own package with no other dependencies, so you can use it outside
a game. Its types are in the `Super` namespace.
