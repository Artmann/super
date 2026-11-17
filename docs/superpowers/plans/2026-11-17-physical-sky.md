# Physical Sky Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Preetham clear sky with a physical sky after Hillaire (2020) that follows the sun through sunset to a night glow, tints the sun's light, and fades far surfaces into the air.

**Architecture:** Three compute tables (transmittance, multiple scattering, sky-view) in `SkyAtmosphere`, recorded with the environment uniforms. `environment.comp` draws the cubes from the sky-view table; the background reads a disc-free cube and adds the disc per pixel. Aerial perspective is closed form in `sky.glsl`, applied by lighting, glass and particles.

**Tech Stack:** C#, Vulkan (Silk.NET), GLSL compute and fragment shaders, xunit.

**Spec:** `docs/superpowers/specs/2026-11-17-skies-design.md`, section 2.

## Global Constraints

- `Turbidity` above zero turns the physical sky on, held to 1.7–10. Zero keeps the gradient.
- A noon sky in the examples stays within a few percent of its Preetham brightness.
- Every new effect gets a `Graphics` switch (`AerialPerspective`); defaults look best.
- No validation messages; no Roe dead code or new health findings.
- Docs change with the code: `rendering.md` "The sky", `design.md` "The clear sky".

## Review Focus

- A sun below the horizon: the sky darkens to the night glow, never black, never NaN.
- Turbidity at both ends of its range (1.7 and 10) gives finite tables and a sane sunset.
- A view with its own haze: haze takes over and the air adds nothing on top.
- Chunk bakes and area bakes use the tinted sun, so baked light matches live light.
- A sky that never sets a turbidity never draws the tables, and no shader reads them unready.

---

### Task 1: Atmosphere model on the CPU

**Files:** Create `src/Rendition/Passes/Atmosphere.cs`, `tests/Super.Tests/AtmosphereTests.cs`.

- [x] Tests: transmittance straight up and at the horizon against hand-worked values; aerosol from turbidity; ground light falls at dusk.
- [x] `Atmosphere.Aerosol(float turbidity)`, `Atmosphere.SunTint`, `Atmosphere.GroundLight`.

### Task 2: The tables and the sky's cubes

**Files:** Create `src/Rendition/Shaders/atmosphere*.{glsl,comp}`, `src/Rendition/Vulkan/SkyAtmosphere.cs`; modify `environment.comp`, `sky.glsl`, `EnvironmentUniforms`, `EnvironmentPrefilter`, `CubeShader`.

- [x] Transmittance and multiple scattering redrawn on a turbidity change, the sky-view on every sky change.
- [x] Preetham code and its C# mirror removed; noon brightness calibrated (`SOLAR_STRENGTH` 13).

### Task 3: Background, sun tint and aerial perspective

**Files:** Modify `lighting.frag`, `transparent.frag`, `particle.frag`, `fog.glsl`, `taa.frag`, `SkyModel.cs`, `Renderer.cs`, `BakeJob.cs`, `Graphics.cs`, `Sky.cs`.

- [x] Background cube without the disc; disc added per pixel.
- [x] `SkyModel.Tinted` in Render, Bake and BakeChunk.
- [x] `Graphics.AerialPerspective`, on in every profile.

### Task 4: Docs, shots, Archer

- [x] `rendering.md`, `design.md`, shots in `docs/media/`.
- [x] Archer moves to the physical sky.
