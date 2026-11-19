# World in Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the entity world a built-in part of Super that every game has, with one-call spawning, required components, changes that queue during queries, reactions, 256 component types and smooth motion by default.

**Architecture:** The `Super.Ecs` project is folded into `Super` (folder `src/Super/World/`, namespace `Super`), and `HostServices` registers the `World` and its hooks for every game. The archetype mask grows from one `ulong` to a 256-bit `ComponentMask`. Structural changes made while a query runs go into a typed, non-boxing queue on the world that is applied when the outermost query ends, and reactions fire as components land or leave.

**Tech Stack:** C# / .NET, xunit, Microsoft.Extensions.DependencyInjection, Roe.

**Spec:** `docs/superpowers/specs/2026-11-19-world-first-design.md`. This plan is stage 1 of its six stages. Stages 2 to 6 each get their own plan, written when the stage before has merged.

## Global Constraints

- One pull request into `main`, from branch `feat/world-in-core`. `main` takes breaking changes for 2.0.
- Braces on every control statement.
- Docs in `docs/` change in the same commit as the code they describe. Before each commit, search `docs/` for the names the task touched.
- `dotnet roe` is clean before each commit. Delete dead code. Rewrite `roe-baseline.json` only when a finding was fixed or moved.
- Conventional commits. No Claude author, no `Co-Authored-By` line, no "Generated with Claude Code" line.
- Every new test is proven able to fail: break the production code, watch it fail, revert.
- A running tick and a running frame allocate nothing. `Spawn`, queued changes and reactions must not box or allocate once their buffers have grown.
- Queries visit entities in an order that depends only on what was done to the world, so a seed replays the same.
- Rebuild before trusting a test result. Never pass `--no-build`.
- Fast tests: `dotnet test Super.slnx -c Release --filter "Speed!=Slow&Needs!=GPU"`.
- The NuGet package stays one package named Super.
- `view.Models(world)` stays in this stage. It is removed in stage 2.

## Review Focus

- An entity destroyed inside a query that also has an `Add` queued after the destroy: the add is dropped without throwing. Pinned in Task 4.
- An entity spawned and then destroyed inside the same query: it is gone when the query ends and its `OnAdded` never fires. Pinned in Tasks 4 and 5.
- An exception thrown from the body of a `foreach` over a query: the queue is still applied and the world is not left thinking a query runs. Pinned in Task 4.
- An `OnRemoved` handler that spawns or destroys other entities, including while the queue is being applied: every change lands exactly once. Pinned in Task 5.
- An entity whose position is wrapped or reset by writing its `Transform` now glides for one tick, where before it jumped unless it was `Smoothed`. `world.Teleport` is the answer, and the examples are checked for it. Pinned in Task 6.

---

### Task 1: Fold `Super.Ecs` into `Super` and register the world for every game

**Files:**
- Move: every `.cs` file in `src/Super.Ecs/` (including `Audio/`) to `src/Super/World/`, keeping file names. Change `namespace Super.Ecs;` to `namespace Super;`.
- Delete: `src/Super.Ecs/Super.Ecs.csproj`, `src/Super/World/EcsServices.cs`, the `src/Super.Ecs` folder.
- Move: `tests/Super.Ecs.Tests/*.cs` to `tests/Super.Tests/World/`, namespace `Super.Tests`. Delete the `tests/Super.Ecs.Tests` project.
- Modify: `Super.slnx`, `src/Super/Super.csproj` (drop the `Super.Ecs` and `Super.Ecs.Tests` `InternalsVisibleTo` lines), `src/Super.Physics/Super.Physics.csproj` (reference `Super`), `src/Super.Physics/PhysicsServices.cs` (drop the `AddSuperEcs` fallback), `examples/Forest/Forest.csproj`, `examples/Reef/Reef.csproj`, `examples/Rooms/Rooms.csproj`, `packaging/nuget/Super.Package.csproj` (drop `Super.Ecs` from `SuperLibrary`), `roe.json` (drop `Super.Ecs` from `libraryProjects`), `src/Super/HostServices.cs`, `src/Super.Testing/HeadlessGame.cs`.
- Modify: every `.cs` file with `using Super.Ecs;` or `AddSuperEcs()` under `src/`, `examples/` and `tests/`.
- Docs: `docs/ecs.md`, `docs/README.md`, `docs/getting-started.md`, `docs/rendering.md`, `docs/views.md`, `docs/audio.md`, `docs/testing.md`, `docs/tweens.md`, `docs/design.md`.
- Test: `tests/Super.Tests/World/WorldServiceTests.cs`

**Interfaces:**
- Produces: `Super.World`, `Super.Entity`, `Super.Transform` and the existing components in namespace `Super`. `HeadlessRun<TGame>.World` of type `World`. `AddSuperEcs()` no longer exists.

- [ ] **Step 1: Branch and commit the design and this plan**

```bash
git checkout -b feat/world-in-core
git add docs/superpowers/specs/2026-11-19-world-first-design.md docs/superpowers/plans/2026-11-19-world-in-core.md
git commit -m "docs: add the world-first design and the stage 1 plan"
```

- [ ] **Step 2: Write the failing test**

```csharp
namespace Super.Tests;

public class WorldServiceTests
{
    private sealed class Mover(World world) : IGame
    {
        public Entity Entity { get; private set; }

        public void Load()
        {
            Entity = world.Create();
            world.Add(Entity, Transform.At(new Vector3(1f, 0f, 0f)));
            world.Add(Entity, new Smoothed());
        }

        public void Update(GameTime time) => world.Get<Transform>(Entity).Position.X += 1f;

        public void Draw(View view) { }
    }

    [Fact]
    public void A_game_gets_the_world_without_registering_it()
    {
        using var run = HeadlessGame.Create<Mover>(seed: 1);

        run.Ticks(1);

        Assert.Equal(2f, run.World.Get<Transform>(run.Game.Entity).Position.X);
    }

    [Fact]
    public void The_world_remembers_last_tick_without_being_registered()
    {
        using var run = HeadlessGame.Create<Mover>(seed: 1);

        run.Ticks(2);

        Assert.Equal(2f, run.World.Get<Smoothed>(run.Game.Entity).Previous.Position.X);
    }
}
```

Match `IGame`'s real members when writing `Mover`; the three above are the ones that matter.

- [ ] **Step 3: Run it to see it fail**

Run: `dotnet test tests/Super.Tests -c Release --filter "FullyQualifiedName~WorldServiceTests"`
Expected: does not compile (`World` unknown in `Super`, no `run.World`).

- [ ] **Step 4: Move the project and register the world**

Do the moves and edits in the Files list. In `HostServices.Build`, before `services.AddSingleton<TGame>()`, register `World` as a singleton, the same instance as `ITickHook` and `IStateHook`, and `SoundSync` as `IDrawHook`, exactly as `EcsServices` did. Add `public World World` to `HeadlessRun<TGame>`, resolved from the host's services. `AddSuperPhysics` keeps working with no `World` registration of its own.

- [ ] **Step 5: Update the docs**

`docs/ecs.md` loses its "add the package and call `AddSuperEcs`" setup and says the world is always there. Every `using Super.Ecs;` and `AddSuperEcs()` in a doc code block goes. `docs/README.md`'s row for the page no longer names `Super.Ecs`. `docs/design.md` says the world lives in `Super` and is registered by `HostServices`. Then run `grep -rn "Super.Ecs\|AddSuperEcs" docs src examples tests packaging roe.json Super.slnx` and expect matches only under `docs/superpowers/`.

- [ ] **Step 6: Run the tests**

Run: `dotnet test Super.slnx -c Release --filter "Speed!=Slow&Needs!=GPU"`
Expected: PASS, including the moved world tests and the two new ones. To prove the second new test can fail, comment out the `ITickHook` registration, see it fail, and put it back.

- [ ] **Step 7: Check the package and Roe, then commit**

Run: `dotnet pack packaging/nuget/Super.Package.csproj -c Release -o packages` and expect it to succeed with no `Super.Ecs.dll` inside. Run `dotnet roe` and expect no new findings.

```bash
git add -A
git commit -m "feat!: make the world part of Super and register it for every game"
```

---

### Task 2: 256 component types

**Files:**
- Create: `src/Super/World/ComponentMask.cs`
- Modify: `src/Super/World/Archetype.cs`, `ArchetypeTable.cs`, `Queries.cs`, `World.cs`, `ScopedEntities.cs`, `SmoothedTransforms.cs`, `ViewExtensions.cs`, and any file in `src/Super.Physics` that builds an archetype mask (check each `1UL <<` there; most are Jolt layers and stay).
- Docs: `docs/ecs.md` (the limit), `docs/design.md`.
- Test: `tests/Super.Tests/World/ComponentMaskTests.cs`, `tests/Super.Tests/World/ManyComponentTypesTests.cs`

**Interfaces:**
- Produces, all `internal`:

```csharp
internal readonly struct ComponentMask : IEquatable<ComponentMask>
{
    public const int MaxTypes = 256;
    public bool IsEmpty { get; }
    public bool Has(int type);
    public ComponentMask With(int type);
    public ComponentMask Without(int type);
    public bool ContainsAll(in ComponentMask other);
    public ComponentMask And(in ComponentMask other);
    /// The lowest set type after <paramref name="after"/>, or -1. Pass -1 to start.
    public int Next(int after);
}
```

`Archetype.Mask` and every mask parameter become `ComponentMask`. `Archetype.MaxComponentTypes` becomes 256. `ArchetypeTable.NextMatching(ref int index, in ComponentMask mask)`.

- [ ] **Step 1: Write the failing tests**

`ComponentMaskTests`: `With(70)` then `Has(70)` is true and `Has(6)` is false; `With(3).With(200).Next(-1)` is 3, `Next(3)` is 200, `Next(200)` is -1; `With(1).With(130).ContainsAll(With(130))` is true and the reverse is false; `With(5).Without(5).IsEmpty` is true; two masks built in a different order are `Equals` and have the same hash code.

`ManyComponentTypesTests` needs many distinct struct types. Make them from one generic struct through reflection:

```csharp
private struct Numbered<T> { public int Value; }

// Numbered<int>, Numbered<Numbered<int>>, ... gives as many distinct types as asked for.
private static Type TypeNumber(int depth);
private static void AddNumbered(World world, Entity entity, int depth);   // world.Add<that type>(entity, default) via MakeGenericMethod
```

```csharp
[Fact]
public void Types_past_the_sixty_fourth_are_told_apart()
{
    var world = new World();
    var filler = world.Create();
    for (int depth = 0; depth < 140; depth++) { AddNumbered(world, filler, depth); }

    var early = world.Create();
    AddNumbered(world, early, 6);
    var late = world.Create();
    AddNumbered(world, late, 70);

    Assert.False(HasNumbered(world, early, 70));
    Assert.True(HasNumbered(world, late, 70));
    Assert.False(HasNumbered(world, late, 6));
}

[Fact]
public void The_257th_component_type_is_refused()
{
    var world = new World();
    var entity = world.Create();
    for (int depth = 0; depth < 256; depth++) { AddNumbered(world, entity, depth); }

    var error = Assert.Throws<TargetInvocationException>(() => AddNumbered(world, entity, 256));
    Assert.Equal("A world holds at most 256 component types.", error.InnerException!.Message);
}
```

With a one-`ulong` mask, type 70 lands on bit 6, so the first test fails today. The second fails today at type 64.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Super.Tests -c Release --filter "FullyQualifiedName~ComponentMask|FullyQualifiedName~ManyComponentTypes"`
Expected: FAIL (compile error for `ComponentMask`; after stubbing, the two world tests fail as described).

- [ ] **Step 3: Implement `ComponentMask` and replace every archetype `ulong` mask**

Four `ulong` fields. An archetype's column array is sized to its highest type plus one, not 256, so small archetypes stay small. The archetype dictionary is keyed by `ComponentMask`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Super.slnx -c Release --filter "Speed!=Slow&Needs!=GPU"`
Expected: PASS. Then run `dotnet test tests/Reef.Tests -c Release` and compare its tick time with `main`; Reef's queries are the hot path this touches.

- [ ] **Step 5: Docs, Roe, commit**

```bash
git add -A
git commit -m "feat: raise a world's limit to 256 component types"
```

---

### Task 3: `Spawn` and required components

**Files:**
- Create: `src/Super/World/WorldSpawn.cs` (a `partial class World`), `src/Super/World/RequiredComponents.cs`
- Modify: `src/Super/World/World.cs` (make it `partial`, apply requirements in `Add`)
- Docs: `docs/ecs.md`
- Test: `tests/Super.Tests/World/SpawnTests.cs`

**Interfaces:**
- Produces:

```csharp
public Entity Spawn<T1>(in T1 first) where T1 : struct;
// ... the same shape up to eight components: Spawn<T1, ..., T8>.

/// Any entity given a TComponent that has no TNeeded gets this one.
public void Require<TComponent, TNeeded>(TNeeded value) where TComponent : struct where TNeeded : struct;
```

A new `World` already requires `Transform.Identity` for `Model` and for `Animated`. Stage 2 adds its own components to that list.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Spawn_gives_an_entity_all_its_components()
{
    var entity = _world.Spawn(new Position(1f), new Velocity(2f), new Health(3));

    Assert.Equal(1f, _world.Get<Position>(entity).X);
    Assert.Equal(2f, _world.Get<Velocity>(entity).X);
    Assert.Equal(3, _world.Get<Health>(entity).Value);
}

[Fact]
public void A_model_spawned_alone_gets_an_identity_transform()
{
    var entity = _world.Spawn(new Model(default));

    Assert.Equal(Transform.Identity, _world.Get<Transform>(entity));
}

[Fact]
public void A_required_component_never_replaces_one_the_entity_was_given()
{
    var placed = Transform.At(new Vector3(4f, 0f, 0f));

    var modelFirst = _world.Spawn(new Model(default), placed);
    var transformFirst = _world.Spawn(placed, new Model(default));

    Assert.Equal(placed, _world.Get<Transform>(modelFirst));
    Assert.Equal(placed, _world.Get<Transform>(transformFirst));
}

[Fact]
public void A_game_can_require_its_own_components_and_they_chain()
{
    _world.Require<Health, Position>(new Position(7f));
    _world.Require<Position, Velocity>(new Velocity(9f));

    var entity = _world.Create();
    _world.Add(entity, new Health(1));

    Assert.Equal(7f, _world.Get<Position>(entity).X);
    Assert.Equal(9f, _world.Get<Velocity>(entity).X);
}

[Fact]
[Trait("Speed", "Slow")]
public void Spawning_allocates_nothing_once_the_world_has_grown()
{
    for (int i = 0; i < 1000; i++) { _world.Spawn(new Position(i), new Velocity(i)); }
    // destroy them all, then measure 1000 more spawns
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 1000; i++) { _world.Spawn(new Position(i), new Velocity(i)); }
    Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
}
```

Follow `tests/Super.Physics.Tests/AllocationTests.cs` for how this repository measures allocation, and put the last test in the collection it uses.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Super.Tests -c Release --filter "FullyQualifiedName~SpawnTests"`
Expected: does not compile (`Spawn`, `Require` unknown).

- [ ] **Step 3: Implement**

`Spawn` puts every listed component on first and fills in required ones afterwards, which is what makes the third test pass in both orders. Requirements are stored per component type ID as a small list of typed "add if missing" entries, so applying them boxes nothing. Requiring a type from itself, or a cycle, throws `InvalidOperationException` naming both types when `Require` is called.

- [ ] **Step 4: Run the tests, update `docs/ecs.md`, run Roe, commit**

`docs/ecs.md` leads with `Spawn` in its first example and describes `Require` and the built-in requirements.

```bash
git add -A
git commit -m "feat: spawn an entity with its components in one call, with required components"
```

---

### Task 4: Changes made during a query are queued

**Files:**
- Create: `src/Super/World/PendingChanges.cs`
- Modify: `src/Super/World/World.cs`, `WorldSpawn.cs`, `ScopedEntities.cs` (it no longer needs to collect before destroying; keep it if simpler)
- Docs: `docs/ecs.md` (replace "collect the changes and make them afterwards")
- Test: `tests/Super.Tests/World/QueuedChangesTests.cs`; delete or rewrite the existing tests in `WorldTests.cs` that expect a throw during a query.

**Interfaces:**
- Consumes: `Spawn`, `Require` from Task 3.
- Produces: no new public members. Behaviour:
  - While any query runs, `Create`/`Spawn` return a live entity at once. It sits in the empty archetype, which no query visits, and its components are queued.
  - `Add` of a type the entity lacks, `Remove` of one it has, and `Destroy` are queued. `Add` of a type it already has still writes the value at once.
  - The queue is applied, in the order the calls were made, when the outermost query ends, including when the loop body throws.
  - A queued entity stays alive, and keeps its old components, until then.
  - A queued change to an entity that is no longer alive when its turn comes is dropped.
  - `Get<T>` for a component that is queued throws `InvalidOperationException` with `"Entity {id}'s {T} was added during a query and arrives when the query ends."` `Has<T>` returns false.
  - `internal bool World.IsQuerying`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Destroying_inside_a_query_takes_effect_when_it_ends()
{
    var first = Create(1f);
    var second = Create(2f);
    int visited = 0;

    foreach (var row in _world.Query<Position>())
    {
        visited++;
        _world.Destroy(first);
        Assert.True(_world.IsAlive(first));
    }

    Assert.Equal(2, visited);
    Assert.False(_world.IsAlive(first));
    Assert.True(_world.IsAlive(second));
}

[Fact]
public void An_entity_spawned_inside_a_query_is_alive_at_once_and_complete_afterwards()
{
    Create(1f);
    Entity spawned = default;

    foreach (var row in _world.Query<Position>())
    {
        spawned = _world.Spawn(new Position(5f), new Health(3));
        Assert.True(_world.IsAlive(spawned));
        Assert.False(_world.Has<Health>(spawned));
    }

    Assert.Equal(5f, _world.Get<Position>(spawned).X);
    Assert.Equal(3, _world.Get<Health>(spawned).Value);
}

[Fact]
public void Reading_a_queued_component_says_why_it_is_not_there()
{
    var entity = Create(1f);

    foreach (var row in _world.Query<Position>())
    {
        _world.Add(entity, new Health(3));
        var error = Assert.Throws<InvalidOperationException>(() => _world.Get<Health>(entity));
        Assert.Equal($"Entity {entity.Id}'s Health was added during a query and arrives when the query ends.", error.Message);
    }
}

[Fact]
public void Changes_wait_for_the_outermost_query()
{
    var entity = Create(1f);

    foreach (var outer in _world.Query<Position>())
    {
        foreach (var inner in _world.Query<Position>())
        {
            _world.Add(entity, new Health(3));
        }

        Assert.False(_world.Has<Health>(entity));
    }

    Assert.True(_world.Has<Health>(entity));
}

[Fact]
public void A_change_queued_for_an_entity_destroyed_first_is_dropped()
{
    var entity = Create(1f);

    foreach (var row in _world.Query<Position>())
    {
        _world.Destroy(entity);
        _world.Add(entity, new Health(3));
        _world.Destroy(entity);
    }

    Assert.False(_world.IsAlive(entity));
    Assert.Equal(0, _world.Count);
}

[Fact]
public void An_entity_spawned_and_destroyed_in_one_query_is_gone_afterwards()
{
    Create(1f);
    Entity spawned = default;

    foreach (var row in _world.Query<Position>())
    {
        spawned = _world.Spawn(new Position(5f));
        _world.Destroy(spawned);
    }

    Assert.False(_world.IsAlive(spawned));
    Assert.Equal(1, _world.Count);
}

[Fact]
public void A_query_that_throws_still_applies_its_changes_and_ends()
{
    var entity = Create(1f);

    Assert.Throws<InvalidOperationException>(() =>
    {
        foreach (var row in _world.Query<Position>())
        {
            _world.Add(entity, new Health(3));
            throw new InvalidOperationException("stop");
        }
    });

    Assert.True(_world.Has<Health>(entity));
    Assert.False(_world.IsQuerying);
    _world.Destroy(entity);
    Assert.False(_world.IsAlive(entity));
}

[Fact]
public void A_handle_to_a_reused_id_is_not_touched_by_a_change_queued_for_the_old_one()
{
    var old = Create(1f);
    Create(2f);
    Entity reused = default;

    foreach (var row in _world.Query<Position>())
    {
        _world.Destroy(old);
        _world.Add(old, new Health(3));
    }

    reused = _world.Spawn(new Position(9f));

    Assert.Equal(old.Id, reused.Id);
    Assert.False(_world.Has<Health>(reused));
}
```

Add an allocation test like Task 3's: after a warm-up pass, a query that queues one `Add`, one `Remove` and one `Destroy` per row over 1,000 rows allocates nothing.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Super.Tests -c Release --filter "FullyQualifiedName~QueuedChangesTests"`
Expected: FAIL with "Entities can't be created, destroyed or change their components during a query."

- [ ] **Step 3: Implement `PendingChanges`**

One ordered list of `(kind, entity, type ID, index)` records, and one typed value list per component type, reached through an abstract base with `Apply(World world, Entity entity, int index)`. That is what keeps queued adds from boxing. `World.EndIteration` applies and clears the queue when the count returns to zero. The queue is walked by index, and applying a record may append more (Task 5's handlers), which are applied in the same pass.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Super.slnx -c Release --filter "Speed!=Slow&Needs!=GPU"`
Expected: PASS.

- [ ] **Step 5: Docs, Roe, commit**

```bash
git add -A
git commit -m "feat: queue world changes made during a query instead of throwing"
```

---

### Task 5: Reactions

**Files:**
- Create: `src/Super/World/Reactions.cs`
- Modify: `src/Super/World/World.cs`, `WorldSpawn.cs`, `PendingChanges.cs`
- Docs: `docs/ecs.md`
- Test: `tests/Super.Tests/World/ReactionTests.cs`

**Interfaces:**
- Produces:

```csharp
public void OnAdded<T>(Action<Entity> handler) where T : struct;
public void OnRemoved<T>(Action<Entity> handler) where T : struct;
```

- `OnAdded<T>` runs when a `T` lands on an entity that had none. Replacing a value does not run it. For `Spawn`, and for an `Add` that brings required components, every component is on the entity before any handler runs.
- `OnRemoved<T>` runs before the component leaves, on `Remove<T>` and on `Destroy`, so the handler can still `Get<T>`. On `Destroy` it runs for each of the entity's components in type ID order.
- Handlers for one type run in the order they were registered.
- Queued changes run their handlers when they are applied.
- A handler may spawn, add, remove and destroy. If it is running inside a query or inside the queue being applied, its changes join the queue.

- [ ] **Step 1: Write the failing tests**

Each as its own `[Fact]`, with these assertions:

- `OnAdded` runs once for `Spawn(new Health(3))`, with the spawned entity, and `Get<Health>` inside the handler reads 3.
- `OnAdded<Health>` on `Spawn(new Health(3), new Position(1f))` can read `Position` inside the handler.
- `OnAdded<Transform>` runs for `Spawn(new Model(default))` (a required component counts).
- `Add` over an existing `Health` does not run `OnAdded<Health>` again: count stays 1.
- `OnRemoved<Health>` runs on `Remove<Health>` and reads the value 3 inside the handler; it does not run for an entity that never had `Health`.
- `OnRemoved<Health>` and `OnRemoved<Position>` both run on `Destroy`, and `IsAlive(entity)` is true inside each.
- Inside a query, `Spawn(new Health(3))` has not run the handler by the end of the loop body, and has run it once after the loop.
- An entity spawned and destroyed inside the same query runs neither `OnAdded` nor `OnRemoved` for components that never landed.
- A handler that destroys another entity:

```csharp
[Fact]
public void A_handler_can_destroy_other_entities()
{
    var torch = _world.Spawn(new Position(1f));
    var holder = _world.Spawn(new Health(3));
    int removed = 0;
    _world.OnRemoved<Health>(_ => _world.Destroy(torch));
    _world.OnRemoved<Position>(_ => removed++);

    foreach (var row in _world.Query<Health>())
    {
        _world.Destroy(holder);
    }

    Assert.False(_world.IsAlive(torch));
    Assert.False(_world.IsAlive(holder));
    Assert.Equal(1, removed);
    Assert.Equal(0, _world.Count);
}
```

- A handler that spawns an entity of the same type it reacts to, guarded to do so once, leaves exactly two entities and a handler count of 2.
- Two handlers for one type run in registration order (record into a list, assert `["first", "second"]`).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Super.Tests -c Release --filter "FullyQualifiedName~ReactionTests"`
Expected: does not compile (`OnAdded`, `OnRemoved` unknown).

- [ ] **Step 3: Implement**

Handlers are stored per component type ID. A world with no handlers for a type pays one array read per structural change of that type and nothing else. While handlers run outside a query, the world counts as querying, so their own changes queue and are applied when the outermost handler returns.

- [ ] **Step 4: Run all fast tests, update `docs/ecs.md`, run Roe, commit**

```bash
git add -A
git commit -m "feat: react when a component lands on or leaves an entity"
```

---

### Task 6: Smooth motion by default, `Teleport`, and no `Smoothed`

**Files:**
- Modify: `src/Super/World/Components.cs` (delete `Smoothed`; add `internal struct LastTick { Transform Previous; float PreviousPhase; bool HasPrevious; }`), `SmoothedTransforms.cs` (rename to `LastTicks.cs`), `ViewExtensions.cs`, `World.cs`, `Audio/SoundSync.cs` if it reads `Smoothed`, `src/Super.Physics/RagdollStanding.cs`
- Modify: every example and test that adds `Smoothed` (19 places: Cannonball, Forest, Reef, Rooms and their tests, `tests/Super.Tests/World/*`, `tests/Super.Physics.Tests/*`)
- Docs: `docs/ecs.md`, `docs/views.md`, `docs/rendering.md`, `docs/audio.md`, `docs/getting-started.md`, `docs/design.md`
- Test: `tests/Super.Tests/World/SmoothMotionTests.cs`; update `ModelsTests.cs` and `WorldServiceTests.cs` from Task 1 (it reads `Smoothed`)

**Interfaces:**
- Consumes: `Require` from Task 3.
- Produces:

```csharp
/// Moves an entity with no glide: it is drawn at the new place on the very next frame.
public void Teleport(Entity entity, Transform transform);
```

- A new `World` requires `LastTick` for `Transform`, so every entity with a `Transform` carries one.
- Before each tick, and when a state is entered, every `Transform` on an entity without `Static` is copied to its `LastTick`. Archetypes with `Static` are skipped.
- Drawing blends from `LastTick` when it has a previous value and the entity is not `Static`. A new entity's first frame shows it where it is.
- `Swim.Phase` blends the same way for every swimming entity.

- [ ] **Step 1: Write the failing tests**

Use a headless game that spawns `Transform.At(Vector3.Zero)` with a `Model` and adds 2 to `Position.X` every tick, and read positions from `run.Drawn`.

```csharp
[Fact]
public void An_entity_is_drawn_between_its_last_two_ticks_without_asking()
{
    using var run = HeadlessGame.Create<Slider>(seed: 1);
    run.Ticks(2);                      // X was 2, is 4

    run.Frame(0.25f);

    Assert.Equal(2.5f, run.Drawn.Single().Position.X, 4);
}

[Fact]
public void A_new_entity_is_drawn_where_it_is_on_its_first_frame()
{
    using var run = HeadlessGame.Create<Slider>(seed: 1);
    run.Ticks(1);
    run.Game.SpawnAt(10f);             // spawned after the tick's remembering

    run.Frame(0.25f);

    Assert.Contains(run.Drawn, drawn => drawn.Position.X == 10f);
}

[Fact]
public void A_teleported_entity_does_not_glide()
{
    using var run = HeadlessGame.Create<Slider>(seed: 1);
    run.Ticks(2);
    run.World.Teleport(run.Game.Entity, Transform.At(new Vector3(100f, 0f, 0f)));

    run.Frame(0.25f);

    Assert.Equal(100f, run.Drawn.Single().Position.X);
}

[Fact]
public void A_static_entity_is_drawn_where_it_is()
{
    using var run = HeadlessGame.Create<Slider>(seed: 1);
    run.World.Add(run.Game.Entity, new Static());
    run.Ticks(2);

    run.Frame(0.25f);

    Assert.Equal(4f, run.Drawn.Single().Position.X);
}
```

Read `ModelDraw` for how a drawn model exposes its position (it may be a matrix) and adjust the member, not the expected numbers. `Slider.Draw` calls `view.Models(world)`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Super.Tests -c Release --filter "FullyQualifiedName~SmoothMotionTests"`
Expected: the first fails with 4 instead of 2.5; `Teleport` does not compile.

- [ ] **Step 3: Implement, and delete `Smoothed` everywhere**

`RagdollStanding` uses `world.Teleport` where it wrote `Smoothed.Previous`. Remove every `new Smoothed()` and `Has<Smoothed>` from examples and tests.

- [ ] **Step 4: Find things that used to jump and now glide**

Entities that were not `Smoothed` now blend. In Cannonball, Forest, Reef and Rooms, find every place that sets a `Transform`'s position to somewhere unrelated to where it was (a wrap at a boundary, a respawn, a reset on restart, a pooled entity reused) and use `world.Teleport` there. For each example, record a short run on `main` and on the branch with the same `--seed`, `--ticks` and `--size` (see `docs/command-line.md`) and watch both for a one-tick smear.

- [ ] **Step 5: Run the tests**

Run: `dotnet test Super.slnx -c Release --filter "Needs!=GPU"`, then `dotnet test tests/Rendition.Gpu.Tests -c Release`.
Expected: PASS, apart from the bake-budget GPU test that already fails on `main`.

- [ ] **Step 6: Docs, Roe, commit**

The docs say every entity with a `Transform` moves smoothly, that `Static` ones don't, and when to call `Teleport`. `grep -rn "Smoothed" docs src examples tests` matches only `docs/superpowers/`.

```bash
git add -A
git commit -m "feat!: smooth every moving entity between ticks and remove Smoothed"
```

---

### Task 7: Check the whole stage and open the pull request

**Files:**
- Modify: `roe-baseline.json` only if a finding was fixed or moved.

- [ ] **Step 1: Run everything CI runs**

Run: `dotnet test Super.slnx -c Release --filter "Needs!=GPU"`, `dotnet test tests/Rendition.Gpu.Tests -c Release`, `dotnet roe`, and `dotnet pack packaging/nuget/Super.Package.csproj -c Release -o packages`.
Expected: all pass; Roe reports no dead code and no new health finding.

- [ ] **Step 2: Compare speed with `main`**

Run Reef with `--stats` for the same `--seed` and `--ticks` on `main` and on the branch, three times each. Update and draw time on the branch are within noise of `main`. If not, profile before going on: the mask (Task 2) and the remembering of every transform (Task 6) are the two suspects.

- [ ] **Step 3: Read the docs once through**

Read `docs/ecs.md` top to bottom as a newcomer. It should show `Spawn` first and mention no package to add, no `AddSuperEcs` and no `Smoothed`.

- [ ] **Step 4: Open the pull request**

Title: `feat!: make the world part of Super`. The description says what changed and why, how it was tested, the Reef timings from Step 2, and what it still can't do (the world doesn't draw itself yet; that is stage 2). Nothing on screen changes, so it has no pictures.
