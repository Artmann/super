# Hierarchy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stage 3 of the world-first spec: destroying an entity destroys everything under it, `Parent` can name a bone of an animated parent, and `world.Children(entity)` lists children.

**Architecture:** Children are found by walking the archetypes that have `Parent` and comparing the parent handle, with no separate index, because `Parent` can be rewritten in place through `world.Get<Parent>`. `World.Destroy` destroys the children after the entity itself, so queued destroys (made when a query ends) cascade too. A bone parent places the child at the parent's joint, worked out from the pose `PoseAhead` seconds past the tick, the same time the skinned model is drawn at.

**Tech Stack:** C# / .NET 10, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-19-world-first-design.md`, section "Hierarchy".

## Global Constraints

- Braces on every control statement.
- `dotnet roe` stays clean: no dead code, no new health findings, duplicate groups no more than 187.
- Docs change in the same commit as the code (`docs/ecs.md`, `docs/animation.md`, `docs/design.md`).
- Conventional commits, with no Claude author and no Co-Authored-By line.
- No lambdas and no allocation on the frame path when nothing is parented to a bone.

## Review Focus

- A cycle of parents (A under B under A): destroying either must end, not overflow the stack.
- A child re-parented in place (`world.Get<Parent>(c) = new Parent(other)`) before its old parent dies must not be destroyed with it.
- A Removed handler on a child that reads its parent: the parent is already gone, and that must not throw inside the engine.
- A bone parent whose parent loses `Animated` later: the throw must name the entity, not crash with a null reference.
- A bone parent read by physics (`BodySync`) at blend 1 must use the pose as of the tick, not one ahead.

---

### Task 1: Destroy cascades, and world.Children

**Files:**
- Create: `src/Super/World/WorldHierarchy.cs`
- Modify: `src/Super/World/World.cs` (`Destroy`)
- Test: `tests/Super.Tests/World/HierarchyTests.cs`

**Interfaces:**
- Produces: `public static class WorldHierarchy` with `public static IReadOnlyList<Entity> Children(this World world, Entity entity)` (a new list, direct children only, in no promised order) and `internal static void DestroyChildren(World world, Entity entity)`.

- [ ] **Step 1: Write the failing tests** in `HierarchyTests`:
  - `Destroying_a_parent_destroys_its_children_and_theirs` (parent → child → grandchild; all three dead).
  - `Destroying_a_child_leaves_its_parent`.
  - `A_destroy_made_in_a_query_takes_the_children_when_the_query_ends` (destroy inside `foreach (var row in world.Query<Transform>())`; child alive inside the loop, dead after).
  - `A_child_moved_to_another_parent_stays` (rewrite `world.Get<Parent>(child) = new Parent(other)`, destroy the old parent; child alive).
  - `A_cycle_of_parents_is_destroyed_without_looping` (A parent B, B parent A; destroy A; both dead).
  - `Removed_handlers_run_for_children` (OnRemoved<Transform> counts 2 for parent + child).
  - `Children_lists_only_direct_children` (sorted ids equal; grandchild not listed; empty for a leaf).
  - `Leaving_a_state_takes_the_children_of_scoped_entities` (child with no ScopedTo dies with its scoped parent; use `ScopedEntities.DestroyScopedTo`).
- [ ] **Step 2: Run** `dotnet test tests/Super.Tests -c Release --filter "FullyQualifiedName~HierarchyTests"`. Expected: build fails (no `Children`), then after a stub, the destroy tests FAIL.
- [ ] **Step 3: Implement.** `DestroyChildren` returns at once when no `Parent` type exists; otherwise collects the matching rows into a list first (rows move as children die), then calls `world.Destroy` on each still alive. `World.Destroy` calls it after `_entities.Free(entity)` on both paths. A cycle ends because a dead entity is skipped.
- [ ] **Step 4: Run** the same filter. Expected: PASS. Break the cascade on purpose, watch the tests fail, revert.
- [ ] **Step 5: Commit** `feat: destroying an entity destroys its children, and world.Children lists them`, with `docs/ecs.md` (Parent row, a "Children" note, ScopedTo children) and `docs/design.md`.

### Task 2: Parent to a bone

**Files:**
- Modify: `src/Super/World/Components.cs` (`Parent`), `src/Super/World/WorldFrame.cs` (`WorldMatrix`), `src/Super/Animation/Animator.cs`, `src/Super/Animation/PoseJointCache.cs`, `src/Super/View.cs` (internal `PoseAhead`), `src/Super/ViewPlacement.cs`, and the `WorldMatrix` callers that have a view (`WorldEmitters`, `WorldLights`).
- Test: `tests/Super.Tests/World/BoneParentTests.cs`

**Interfaces:**
- Consumes: Task 1 nothing.
- Produces: `public record struct Parent(Entity Value, string? Bone = null);`, `internal static Matrix4x4 WorldMatrix(World world, Entity entity, float blend, float poseAhead = 0f)`, `internal Matrix4x4 Animator.JointAhead(int joint, float ahead)`.

- [ ] **Step 1: Write the failing tests** with `TestRig` (`Move` slides the root 2 units a second along x):
  - `A_child_on_a_bone_follows_the_joint` (parent animator in Move at the start, child `Parent(fighter, Bone: "child")` with local translation (0,0,1); `view.TransformOf(child).Translation` is (0,1,1) with the parent at the origin, and adds the parent's own translation).
  - `A_child_on_a_bone_follows_the_pose_between_ticks` (`view.Begin(blend: 0.5f, tick: 1, tickSeconds: 0.5f)` gives PoseAhead 0.25 s, so the root joint is at x 0.5).
  - `A_bone_the_rig_lacks_throws_naming_it` (`ArgumentException`, message contains `"hand_l"` and `$"Entity {child.Id}"`).
  - `A_bone_on_a_parent_that_isnt_animated_throws` (`InvalidOperationException`, message names the child and parent ids).
  - `Physics_places_a_bone_child_as_of_the_tick` (`WorldFrame.WorldMatrix(world, child, 1f)` equals the joint at ahead 0).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~BoneParentTests"`. Expected: build fails on `Bone:`.
- [ ] **Step 3: Implement.** `PoseJointCache` gains the ahead it was worked out for, so the Animator keeps two caches: `JointCache` at 0 (what `Joint(name)` uses) and one for drawing. `WorldMatrix` multiplies a bone child's local matrix by the joint matrix before the parent's world matrix. `ModelRows.Draw` passes `view.PoseAhead`.
- [ ] **Step 4: Run** the filter. Expected: PASS. Break the ahead on purpose, watch the between-ticks test fail, revert.
- [ ] **Step 5: Commit** `feat: Parent can name a bone of an animated parent`, with `docs/animation.md` (attach to a bone), `docs/ecs.md`, `docs/design.md`.

### Task 3: Stage check

- [ ] Run `dotnet roe`. Expected: no dead code, no health issues, duplicates no more than 187.
- [ ] Run `dotnet test Super.slnx -c Release --filter "Needs!=GPU"`. Expected: all pass.
- [ ] Grep `docs/` for `Parent(` and `ScopedTo` so no old description is left.
- [ ] Push the branch.
