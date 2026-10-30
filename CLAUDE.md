# Rendition

A deferred PBR renderer in .NET 10 and Vulkan 1.3, and the rendering foundation
of the owner's future game engine. `README.md` has the status and commands.
`docs/design.md` has the decisions and known simplifications; update it when a
decision changes.

## Working with the owner

- The owner is not a rendering expert. Make the technical calls and explain them
  in plain words.
- Scope is rendering in a window. No editor, physics, audio or game logic. Only
  Windows for now, but keep Linux possible.
- Roadmap order: lighting, textures, post-processing, custom shaders.

## Rules

- `Renderer.Render` must not allocate managed memory, except when the window
  resizes. Allocate up front, at startup or when loading.
- Build each feature through the real pipeline, never as a throwaway demo.
- New post-processing effects should be `FullscreenPass` instances.
- The library must not depend on a windowing library. The window connects through
  `RendererOptions`.
- Keep shader data blocks in step with their C# structs: `FrameUniforms` matches
  `Shaders/common.glsl`, and `DrawConstants` matches the gbuffer shaders.
- Warnings are errors. Leave them on.

## Verifying a change

Build, then run both:

```
dotnet run -c Release --project src/Rendition.Sample -- --check
dotnet run -c Release --project src/Rendition.Sample -- --validate
```

`--check` must exit 0 with 0 bytes allocated and 0 validation messages. It can't
tell whether the picture is right, so look at the running window or a screenshot
too. Test resizing after changing render targets or the swapchain.

If `VULKAN_SDK` is missing from the shell, read it from the machine environment:
`$env:VULKAN_SDK = [Environment]::GetEnvironmentVariable('VULKAN_SDK','Machine')`

## Commits

Use conventional commits (`feat:`, `fix:`, `docs:`, `chore:`). Don't list Claude
as an author and don't add Co-Authored-By lines.
