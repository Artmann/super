# Asset pipeline design

A game has models, textures, images, sounds and JSON. Today Super reads two places in
two ways. `content/` holds JSON, PNGs, JPEGs and sounds that are read and decoded at
runtime: textures go through StbImageSharp on every load, and a texture's colour space
is passed in code (`content.Texture(path, TextureKind.Linear, normalMap: true)`).
`assets/source` holds models that someone cooks by hand, one `Rendition.Import` command
per model, with settings in `assets/*.materials.json`, into `assets/cooked`. The game
finds that folder by walking up from the program. Nothing knows when a model is stale,
and a build server can't cook without a script that copies those commands.

This replaces both with one pipeline. It runs as part of `dotnet build`, on a build
server, and in watch mode while the game runs.

## The idea that matters most

**Cooking is a pure function:** `cooked = f(source bytes, settings, cooker version, target)`.
A cooker reads only what it's given and writes the same bytes every time: no
timestamps, no absolute paths, no ordering that depends on the disk. Everything else
follows from this rule. That includes incremental builds, the cache, sharing cooked
data over the network, build servers, and repeatable builds. A design choice that
breaks it breaks all of them.

## Decisions already made

- **Where big sources live is up to the user.** Sources can sit in the game's repo, in
  Git LFS, in another checkout or on a network share. The pipeline only needs a folder
  path. It hashes bytes, never paths, so the cache works wherever the files are.
- **Assets are named by their source path, extension included**, relative to the
  content root: `content.Model("models/fox.glb")`. You name the file you can see.
- **Always cook.** The runtime reads only cooked output, in development, in tests and
  in shipped games. No game ever decodes a PNG or parses an FBX.

## What the game writes

```csharp
public class Forest(Content content) : IGame
{
    readonly ModelRef fox = content.Model("models/fox.glb");
    readonly TextureRef ground = content.Texture("textures/forest-floor.jpg");
    readonly TextureRef groundNormal = content.Texture("textures/forest-floor-normal.jpg");
    readonly SoundRef hammer = content.Sound("sounds/hammering.wav");
    readonly GameImage icon = content.Image("icons/axe.png");
    readonly Catalog<Item> items = content.LoadAll<Item>("items");
}
```

The calls stay as they are. What changes is that colour space and normal-map handling
move out of code and into cook settings, so `TextureKind` and `normalMap:` leave
`content.Texture`. A model named in JSON is written the same way:
`"model": "models/axe.glb"`.

The project file:

```xml
<Import Project="path\to\Super.targets" />
<ItemGroup>
  <!-- Optional. Defaults to "content" beside the project. Can be any folder, and more than one. -->
  <SuperContent Include="content" />
  <SuperContent Include="..\..\art\content" />
</ItemGroup>
```

`dotnet build` cooks what changed. `dotnet publish` copies the cooked output beside the
program.

## Layout

```
content/                          the content root: sources plus settings
  cook.json                       folder rules for everything under content/
  models/
    fox.glb
    fox.glb.cook.json             optional per-file settings (today's materials.json)
    textures/fox_diffuse.png      read by the fox, not cooked on its own (see rules)
  textures/forest-floor.jpg
  sounds/hammering.wav
  items/axe.json
bin/<config>/content/             cooked output: content.manifest and objects/<hash>, never in git
bin/<config>/content.dev.json     only for a game run from source; never published
```

### Settings: folder rules and sidecars

A `cook.json` in any folder applies to everything below it. Its rules match globs, and
for a given setting a later rule beats an earlier one, and a deeper `cook.json` beats a
shallower one:

```json
{
  "rules": [
    { "match": "**/*-normal.*", "texture": { "normalMap": true } },
    { "match": "ui/**", "texture": { "mipmaps": false } },
    { "match": "models/**/textures/**", "cook": false },
    { "match": "music/**", "sound": { "stream": true } }
  ]
}
```

A file named `<source>.cook.json` beside a source holds settings for that one file, and
beats every rule. Its contents are what `materials.json` holds today.

Because rules can target paths from any folder above, settings can live in the game's
repo even when big sources sit in an ignored folder or somewhere else. Neither
`cook.json` nor `*.cook.json` is ever a content record, so `LoadAll` skips them.

Unknown settings are cook errors. That way a typo can't silently drop a setting. A
sidecar beside a missing file is a warning (`No source file <name>; skipped.`), so a
clone without sources kept out of git, such as licensed models, still builds. A rule that
matches nothing is not reported.

### Conventions: what gets cooked as what

Every file under the root is an asset, chosen by extension, unless a rule says
`"cook": false`:

| Extension | Cooker | Cooked form |
|---|---|---|
| `.glb .gltf .fbx .obj` | Model | `.rmodel`, as today |
| `.png .jpg .jpeg .tga` | Texture | Mipmapped, GPU-ready pixels, sRGB unless set linear (normal maps are linear) |
| `.wav .ogg .mp3` | Sound | The file itself, once it's been decoded to prove it plays. WAV, Ogg and MP3 are playback formats, and long clips already stream from them |
| `.json` | Data | The file itself, once it's been checked as JSON. It isn't rewritten, so line numbers in load errors still match the source |
| `.spv` / `.frag` | Shader | Fits here later. `Shaders.targets` stays until then |

An extension with no cooker is a cook error that names the file, unless a rule turns
cooking off for it. Some files are never assets: `.bin` (a glTF's buffers, which the
model reads), `.md`, `.txt`, and anything in a folder or file whose name starts with a
dot, such as `.schemas`. Images for the interface (`content.Image`) and textures for models
(`content.Texture`) come from the same texture cooker with different defaults, set by
rules (for example `ui/**` gets no mipmaps).

### More than one root

A project can list several roots. The examples all list their own `content/` and the
shared `assets/` at the top of the repo. Names from every root share one space, and the
same name in two roots is a cook error naming both files. This isn't modding. Nothing
replaces anything. A `cook.json` applies only inside its own root.

### Parts of a source

Some sources hold several assets, such as Poly Haven's plant sets with variants side by
side. A sidecar lists them under `parts`, and each part is named with `#`:

```json
// assets/archer-props/firs.glb.cook.json
{
  "parts": {
    "tall":  { "node": "fir_tall",  "materials": [{ "match": "twigs", "alphaCutoff": 0.5, "foliage": true }] },
    "young": { "node": "fir_young", "materials": [{ "match": "twigs", "alphaCutoff": 0.5, "foliage": true }] }
  }
}
```

```csharp
var tall = content.Model("archer-props/firs.glb#tall");
```

A source with `parts` cooks only its parts. Naming a part that isn't listed is a
`ContentException` that lists the parts there are.

## Names

A name is the path from the content root, with forward slashes and the extension. Names
are compared ordinally and are **case-sensitive on every OS**. A reference that differs
from the real file only in case is a cook error on Windows too, so it can't work on one
machine and break on a Linux build server.

## The manifest

Cooking writes `content.manifest` beside the cooked files. It maps each name to its
kind, the hash and size of its cooked bytes, and the names it refers to. Cooked files
are stored by hash, so two identical sources are stored once. At startup `Content`
reads the manifest. After that, naming an asset that isn't in it is a
`ContentException` naming the asset, as missing files are today.

Since everything is cooked, a model path in JSON is checked against the manifest every
time. Today's "checked when `assets/cooked` exists" goes away.

## Cookers

```csharp
public interface ICooker
{
    string Id { get; }            // "texture", part of the cache key
    int Version { get; }          // bump when output changes; invalidates the cache
    void Cook(CookInput input, CookOutput output);
}
```

`CookInput` gives the source bytes, the merged settings and the target. It also has
`Read(path)` for other files: a model's textures, its extra animation files. **Every
file a cooker reads goes through `Read`**, and the pipeline records it as an input.
A cooker that opens files on its own breaks incremental cooking, so tests check each
built-in cooker against this rule.

`CookOutput` takes the cooked bytes, the names the asset refers to, and the errors and
warnings it found. Errors carry a file and a line where there is one, in the format
`ContentException` already uses.

XNA split each step into importer → processor → writer. Here that's one cooker per
kind, because every asset kind in Super has exactly one source → cooked path.

A game adds cookers for its own formats, such as Tiled or LDtk levels, in a tools
project that the build points at. That comes in step 5 below.

## The cache

The cache key is a SHA-256 over the cooker id and version, the target, the settings in
canonical form, and the content hash of every input. Inputs are found while cooking,
so the cache stores a short record per source, keyed by the source's hash, settings,
cooker and target. The record lists the inputs read last time and their hashes. If
those inputs still hash the same, the output's key is known without cooking. This is
how ccache's "manifest mode" works.

- **Local cache:** a folder, `~/.super/cache` by default, or `SUPER_CACHE`. Shared by
  every project and branch on the machine, so switching branches doesn't re-cook.
- **Remote cache:** optional. A base URL that answers `GET` and `PUT` on `/<key>`, so
  a static file server, nginx or an S3 bucket all work. A build server writes to it,
  and developers read from it, so a teammate or CI's cook is downloaded instead of
  redone.

Distributed cooking (farming cooks out to other machines) isn't planned. A shared
cache gives most of the benefit for far less work.

## Targets

The target is part of every key. There's one target to start, `desktop`, with BC7
colour and BC5 normal maps. A later `mobile` target could use ASTC. A target never
changes names or the manifest's shape, only the cooked bytes.

## Running it

- **`dotnet build`.** An MSBuild target (from a `Super.Build` package) runs the cooker
  after compile, passing `$(SuperContent)`, the target and the output folder. It's
  incremental through the cache, so a build with nothing changed cooks nothing. Cook
  errors are build errors and show in the IDE with file and line.
- **`super-cook`.** The same cooker as a .NET tool:
  `super-cook --content art/content --out out/content --target desktop`, for build
  servers and scripts. `--verify` cooks everything twice, with no cache, and fails if
  any bytes differ. That keeps the pure-function rule honest.
- **Watch mode.** A game run from source starts `super-cook --watch` as a child
  process. Saving a source file re-cooks it, and the game picks up the changed manifest
  at the start of the next tick, the same way JSON live reload works now. A failed cook
  prints its errors and keeps the old asset. Watch mode is off in headless runs,
  `--screenshot`, `--record`, `--profile` and shipped games, as live reload is now.

The game never links the cookers, so a shipped game carries no Assimp or texture
compressor, and development runs the same cooking code a build server does.

## Tests

Tests keep stating content next to their checks. `MemoryContent` holds **sources**, and
`HeadlessGame` cooks them in memory with the same cookers before the run, so a test goes
through the same path as a game. Data, sound and texture cooking is managed code and
fast. Headless runs never read a model's bytes, so `MemoryContent.AddModel(name)` adds
a model by name only. A test project that needs a game's real content imports
`Super.targets` and lists the game's roots, and the build cooks them into the test's
output. `tests/Rooms.Tests` does this instead of linking raw JSON.

`run.ContentChanged()` keeps working: it re-cooks the changed files in memory.

## Projects

- `Super.Cook`: the pipeline, settings, manifest, cache and the managed cookers (data,
  ambience, texture, sound). It has no native dependencies.
- `Super.Cook.Models`: the model cooker, moved out of `tools/Rendition.Import`. It's
  the one assembly that needs Assimp.
- `tools/Super.Cook.Tool`: the `super-cook` command, including watch mode.
- `Super.Build`: the MSBuild targets.
- `Super`: reads the manifest and cooked files, starts watch mode, and no longer
  decodes images or sounds itself.

## Steps

1. **Roots, the manifest, conventions, the local cache, build integration and watch
   mode.** Move the model importer behind `ICooker`, turn `materials.json` into
   sidecars, and cook data, sounds and textures (no mipmaps or compression yet).
   `Content` reads only the manifest. The examples move to the shared `assets/` folder
   and `assets/cooked` goes away. The cache is in this step because, without it, every
   build would cook every model again. Watch mode is in it because, without it, JSON
   live reload would stop working. Watch mode reloads JSON, as live reload does now.
2. **Hot reload of textures, images, sounds and models** while the game runs.
3. **Real textures:** mipmaps, BC7/BC5 compression, and sRGB vs linear from rules.
4. **Remote cache and `--verify`.** Pack files for shipping if loose files turn out to
   be slow.
5. **Game cookers** from a game's own tools project.

## Docs

`content.md` covers the content root, names, rules, sidecars and shipping.
`rendering.md#models` stops describing the import command and points at sidecars.
`command-line.md` documents `super-cook`. `getting-started.md` changes "how to cook
them". `design.md` explains the cache key, the manifest and why cooking is a pure
function. The design table's "Model import" row is updated.

## Not now

- Typed names from a source generator (`Assets.Models.Fox`). Names are checked at cook
  time already. This only adds autocomplete.
- Checking JSON records against their C# types at cook time. That needs the cooker to
  load the game's assembly. Records stay checked when they load, as now, with every
  error listed.
- Mods: more than one content root, layered by id.
- Cooking only what the game reaches. Names are strings in code, so the pipeline can't
  know what's used. A rule can turn cooking off for unused files.
- A way for programmers to fetch cooked output without having the sources.

## Settled

- **Watch mode is a child process.** The build writes `content.dev.json` beside the
  cooked output. It names the source roots and the cook tool, and it's never published.
  A game that finds this file knows it's running from source. It starts
  `super-cook --watch` and watches `content.manifest` for changes. That replaces
  today's "the content folder isn't beside the program" check.
- **The examples share `assets/`** at the top of the repo, and each keeps its own
  `content/` for its own JSON and sounds. The big binaries in `assets/` stay out of
  git, and its `cook.json` and `*.cook.json` files are in git.
