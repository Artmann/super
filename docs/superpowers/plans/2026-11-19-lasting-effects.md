# Lasting effects as components Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stage 6 of world-first Super: decals, fog volumes, probes, baked lighting, water, puddles and wind become components the world puts in each frame, so no example needs a `view` call for a lasting thing.

**Architecture:** The same as `WorldLights`. One-per-frame settings are tracked by `Newest` and passed through their own view call, so the checks and messages stay the view's. Placed things take their box or transform from the entity's blended world matrix and use the entity's ID as their key. Hidden entities are skipped.

**Tech Stack:** C# / .NET 10, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-19-world-first-design.md`, stage 6 and "The world draws itself".

## Global Constraints

- Braces on every control statement. CRLF files stay CRLF.
- Docs change in the same commit as the code: the user guides lead with the component, and "Drawing by hand, `view.…`" follows.
- `dotnet roe health` and `dotnet roe dead-code` show ✓; duplicates stay at or below 187.
- Conventional commits, with no Claude author and no Co-Authored-By lines.
- Components must not share a name with a public `Rendition` type, since games use both namespaces.

## Components

The one-per-frame settings, where the newest wins (in `src/Super/World/FrameSettings.cs`, all tracked in `Newest`):

| Component | View call |
| --- | --- |
| `ScreenBounce(float Strength = BounceSettings.DefaultStrength, float Radius = BounceSettings.DefaultRadius)` | `view.Indirect.ScreenBounce` |
| `LightProbes(Vector3 Min, Vector3 Max, float Spacing)` | `view.Indirect.LightProbes` |
| `BakedLighting(string Name, Vector3 Min, Vector3 Max, float ProbeSpacing, int TexelsPerMetre = BakeSettings.DefaultTexelsPerMetre)` | `view.Indirect.BakedLighting` |
| `Underwater(UnderwaterSettings Settings)` | `view.Surroundings.Water` |
| `Puddles(float Level, float Band = PuddleSettings.DefaultBand)` | `view.Surroundings.Puddles` |
| `Wind(Vector3 Direction, float Strength, float Gustiness = WindSettings.DefaultGustiness)` | `view.Surroundings.Wind` |

The placed things, one per entity with a `Transform` or `Parent` (in `src/Super/World/Placed.cs`, described by `src/Super/World/WorldPlaced.cs`):

| Component | View call | Placed by |
| --- | --- | --- |
| `Decal(Surface Material, DecalBlend? Blend = null)` | `view.Effects.Decal(entity.Id, matrix, …)` | The world matrix is the box |
| `FogVolume(FogShape Shape, FogVolumeSettings Settings)` | `view.Effects.FogVolume(entity.Id, …, matrix, …)` | The world matrix places the unit shape |
| `ReflectionCapture(Vector3 BoxMin, Vector3 BoxMax, float Blend = 0.5f, float Priority = 0f, ProbeUpdate Update = ProbeUpdate.OnChange, int Version = 0)` | `view.Indirect.ReflectionProbe(entity.Id, …)` | The photo is taken from the entity's place. The box is measured from there, without turning |
| `BakedPiece(string Name, Vector3 Min, Vector3 Max, int TexelsPerMetre = BakeSettings.DefaultTexelsPerMetre, float EdgeBlend = Rendition.BakedChunk.DefaultEdgeBlend)` | `view.Indirect.BakedChunk(Name, matrix, …)` | The world matrix places it, and may only turn and move |

`ReflectionCapture` and `BakedPiece` aren't called `ReflectionProbe` and `BakedChunk` because those are public `Rendition` types.

## Review Focus

- A placed entity with a parent follows the parent, the same as a light.
- A hidden entity's decal, fog, probe or piece isn't drawn.
- A bad value throws with the view call's message, at the frame that meets it.
- A `Draw` call after the world still overrides or adds, as with the sky.
- A probe box that doesn't contain the entity's place throws the view's "must be inside its box" message.

---

### Task 1: One-per-frame settings as components

**Files:** `src/Super/World/FrameSettings.cs`, `src/Super/World/Newest.cs`, `src/Super/World/WorldLights.cs` (its `Settings`, split so Roe stays green), `tests/Super.Tests/World/FrameSettingsTests.cs`, `docs/rendering.md`, `docs/views.md`, `docs/content.md`

- [ ] Tests: `Screen_bounce_sets_the_frames_bounce`, `Light_probes_set_the_frames_probe_box`, `Baked_lighting_names_the_frames_baked_space`, `Underwater_puts_the_frame_underwater`, `Puddles_set_the_frames_water_level`, `Wind_sets_the_frames_wind`, `The_newest_wind_wins`, and `A_bad_wind_throws_with_the_view_message` (message starts "The wind's strength must be 0 or more metres"). Watch them fail to compile, then fail.
- [ ] Add the components, track them in `Newest`, and pass each through its view call.
- [ ] Docs: each section in `rendering.md` (screen bounce, light probes, baked lighting, water, puddles, wind) leads with `world.Spawn(new X(...))`, then a "Drawing by hand" line. `views.md` table rows and `content.md` name the components.
- [ ] Commit: `feat: screen bounce, light probes, baked lighting, water, puddles and wind as components`.

### Task 2: Placed things as components

**Files:** `src/Super/World/Placed.cs`, `src/Super/World/WorldPlaced.cs`, `src/Super/World/WorldFrame.cs`, `tests/Super.Tests/World/PlacedTests.cs`, `docs/rendering.md`, `docs/views.md` (the Decals and Fog volumes sections move to `rendering.md`, led by the component)

- [ ] Tests:
  - `A_decal_takes_its_box_from_the_entity` checks that `DrawnDecals[0].Box` is the transform's matrix and that its key is the entity ID.
  - `A_fog_volume_follows_its_parent`.
  - `A_reflection_capture_is_photographed_from_the_entity` checks a position of (3,1,2) and a box of (2,0,1)..(5,4,4) for BoxMin (-1,-1,-1) and BoxMax (2,3,2).
  - `A_baked_piece_is_placed_where_the_entity_stands`.
  - `A_hidden_decal_is_not_drawn`.
  - `A_reflection_capture_outside_its_box_throws_with_the_view_message`.
- [ ] Add the components and `WorldPlaced.Describe`, and call it from `WorldFrame.Describe` after the lights.
- [ ] Docs, then commit: `feat: decals, fog volumes, reflection captures and baked pieces as components`.

### Task 3: Examples on components

**Files:** `examples/Courtyard/CourtyardGame.cs`, `CourtyardYard.cs`, `LeavesGame.cs`, `examples/Crypt/CryptGame.cs`, `examples/Gallery/GalleryGame.cs`, `ReflectionProbes.cs`, `examples/Hamlet/HamletGame.cs`, `examples/Reef/ReefGame.cs`, and their tests

- [ ] Move every lasting call to an entity spawned in `Load` (or where the thing is made). Reef's wind surge writes `world.Get<Wind>(wind)` in `Update`.
- [ ] Shots with `--seed 7 --ticks 120 --size 1280x720 --validate` match the before shots, with no new validation messages.
- [ ] Commit: `refactor: Courtyard, Crypt, Gallery, Hamlet and Reef place their effects as entities`.

### Task 4: Stage check

- [ ] Run the full suite with `--filter "Needs!=GPU"`, then the GPU tests, `dotnet roe health` and `dotnet roe dead-code`. Then push.
