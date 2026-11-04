# Asset pipeline (step 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every asset a Super game uses is cooked from source roots into a manifest plus
objects stored by hash. The game reads nothing else. Cooking runs during `dotnet build`,
from a `super-cook` command, and in watch mode that keeps JSON live reload working.

**Architecture:** There's a new `Super.Cook` library: the pipeline, settings, cache, and
the data, texture and sound cookers. `Super.Cook.Models` holds the Assimp model cooker,
moved out of `tools/Rendition.Import`. `tools/Super.Cook.Tool` is the `super-cook`
command, and `src/Super.Build/Super.targets` runs it from MSBuild. `Super` keeps only
the readers: `ContentManifest`, `CookedTexture` and `CookedFolder`. `Super.Cook`
references `Super`, never the other way round. `Super.Testing` references `Super.Cook`,
so `MemoryContent` cooks in memory.

**Tech Stack:** .NET 10, System.Text.Json, StbImageSharp, NVorbis and NLayer (through
`SoundClip`), Silk.NET.Assimp, MSBuild targets, xunit.

**Spec:** `docs/superpowers/specs/2026-11-04-asset-pipeline-design.md`, step 1.

## Global Constraints

- Cooking is a pure function. Output bytes never contain timestamps or absolute paths,
  and never depend on the order the disk lists files in. Sort with `StringComparer.Ordinal`.
- A name is the path from its root, with forward slashes and the extension, and parts
  are written `#part`. Names are compared ordinally and are case-sensitive on every OS.
- The runtime reads only `content.manifest` and `objects/<hash[0..2]>/<hash>`. It never
  decodes PNG or JPEG files, and never parses glTF or FBX.
- Settings files are `cook.json` (folder rules) and `<source>.cook.json` (a sidecar).
  Neither is ever an asset.
- Never assets: `.bin`, `.md`, `.txt`, and any file or folder whose name starts with `.`.
- The default target is `desktop`. The local cache is `~/.super/cache`, or `SUPER_CACHE`
  when that's set.
- Hashes are lowercase hex SHA-256.
- Cook errors print in MSBuild's canonical format, `<full path>(<line>): error SUPER001: <message>`
  (leave out `(<line>)` when there's no line), so the IDE shows them.
- Braces on every `if` and `while`, as `CLAUDE.md` asks. `TreatWarningsAsErrors` is on.
- Commits use conventional messages with no Co-Authored-By lines.
- Each task updates the docs it touches, in the same commit. Task 10 is a final sweep.
- Work on a new branch, `feat/asset-pipeline`, taken from `main` once
  `feat/procedural-joints` is merged.

## Review Focus

1. **A source renamed only in case** on Windows (`fox.glb` → `Fox.glb`). The manifest
   must carry the new case, and code naming the old case must fail. Test in Task 2.
2. **A source deleted between cooks.** It leaves the manifest, its object is pruned, and
   naming it in the game is a `ContentException`. Test in Task 2.
3. **A file still being saved during a watch cook** (`IOException`). The watch keeps the
   old manifest, retries after the next quiet period, and doesn't crash. Test in Task 6.
4. **A `#part` that isn't listed** (`firs.glb#tal`). The game's error lists the parts
   that exist. Test in Task 7.
5. **The same name in two roots**, and a `cook.json` in one root that must not affect
   the other. Test in Task 1.

---

### Task 1: `Super.Cook` project, names, sources and settings

**Files:**
- Create: `src/Super.Cook/Super.Cook.csproj` (references `Super`; add it to `Super.slnx`;
  add `<InternalsVisibleTo Include="Super.Cook" />` and `Super.Cook.Tests` to `src/Super/Super.csproj`)
- Create: `src/Super.Cook/SourceRoot.cs`, `src/Super.Cook/CookSettings.cs`, `src/Super.Cook/Glob.cs`
- Create: `tests/Super.Cook.Tests/Super.Cook.Tests.csproj` (xunit, the same packages as
  `Super.Tests`), plus `tests/Super.Cook.Tests/SourceTests.cs` and `tests/Super.Cook.Tests/SettingsTests.cs`

**Interfaces:**
- Produces:
  - `public interface ISourceRoot { string Label { get; } IEnumerable<string> Files(); byte[] Read(string relative); string? FullPath(string relative); }`.
    `Files()` yields relative paths with `/`. It leaves out dot-files and dot-folders.
    `Label` names the root in errors.
  - `public sealed class FolderRoot(string folder) : ISourceRoot`
  - `public sealed class MemoryRoot : ISourceRoot` with an indexer `this[string] = string`,
    `Add(string, byte[])`, `Remove(string)`, and a `Label` of `"memory"`. `FullPath`
    returns null.
  - `public sealed record SourceFile(ISourceRoot Root, string Name)`
  - `public static class Sources { public static (IReadOnlyList<SourceFile> Files, IReadOnlyList<CookProblem> Problems) Gather(IReadOnlyList<ISourceRoot> roots); }`.
    It leaves out settings files, `.bin`, `.md` and `.txt`, sorts ordinally, and reports
    the same name in two roots.
  - `public sealed record CookProblem(string File, int? Line, string Message, bool IsError)`.
    `ToString()` gives the canonical MSBuild line. `File` is the full path when there is
    one, otherwise `<label>:<name>`.
  - `public static class CookSettings { public static (bool Cook, JsonObject Settings) For(SourceFile file, string cookerId, IReadOnlySet<string> cookerIds, List<CookProblem> problems); }`.
    `cookerIds` are the rule keys allowed besides `match` and `cook`. It merges the rules of every `cook.json` from the root down to the file's folder.
    Each rule is `{ "match": glob, "cook": bool?, "<cooker id>": { ... } }` and the
    `match` is relative to that `cook.json`'s folder. A later rule beats an earlier one,
    and a deeper file beats a shallower one, merging per cooker key with later keys
    overwriting earlier ones. A sidecar `<name>.cook.json` lays its whole object over the
    file's cooker key last. The method returns the settings under `cookerId`.
  - `internal static class Glob { public static bool Matches(string pattern, string relative); }`.
    `*` matches within one segment and `**` matches across segments. Matching is ordinal.

- [ ] **Step 1: Write the failing tests**

`SourceTests` (temp folders, deleted in `Dispose`):
- `Gather_lists_files_ordinally_with_forward_slashes`. Files `b/z.png`, `a.json` and
  `b/a.wav` give `["a.json", "b/a.wav", "b/z.png"]`.
- `Gather_skips_settings_buffers_notes_and_dot_folders`. With `cook.json`,
  `m.glb.cook.json`, `m.bin`, `notes.md`, `.schemas/x.json` and `m.glb`, only `m.glb`
  is listed.
- `Gather_reports_a_name_in_two_roots`. Two `FolderRoot`s each hold `icons/axe.png`.
  Expect one error whose message contains both full paths and `icons/axe.png`.
- `Gather_keeps_the_case_on_disk`. A file saved as `Fox.glb` is listed as `Fox.glb`.

`SettingsTests`:
- `Later_rule_beats_earlier`. The rules `{match:"**/*.png", texture:{linear:true}}` and
  then `{match:"ui/**", texture:{linear:false}}` give `ui/a.png` → `linear == false`.
- `Deeper_cook_json_beats_shallower`.
- `Sidecar_beats_every_rule`.
- `Cook_false_turns_a_file_off`.
- `Rules_match_relative_to_their_own_folder`. A rule `"*.png"` in `ui/cook.json` matches
  `ui/a.png` and doesn't match `a.png`.
- `A_cook_json_in_one_root_does_not_touch_another`. Review Focus 5.
- `A_sidecar_beside_a_missing_file_is_an_error`. The message names the sidecar.
- `Unknown_rule_key_is_an_error`. With `{match:"*", textur:{}}`, the error message
  contains `textur`.

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test tests/Super.Cook.Tests`
Expected: the build fails because the types don't exist yet.

- [ ] **Step 3: Implement the types above.** Read JSON with comments and trailing
  commas allowed (`JsonCommentHandling.Skip`, `AllowTrailingCommas`), as `ContentReader`
  does.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/Super.Cook.Tests`
Expected: all of them pass. Then break `Glob` on purpose (treat `**` as `*`) and watch
`Rules_match...` and `Later_rule...` fail. Revert.

- [ ] **Step 5: Commit**

```bash
git add src/Super.Cook tests/Super.Cook.Tests Super.slnx src/Super/Super.csproj
git commit -m "feat: gather sources and merge cook settings from roots"
```

---

### Task 2: The pipeline, the manifest, the store and the data cooker

**Files:**
- Create: `src/Super/Data/ContentManifest.cs` (in `Super`, because the runtime reads it)
- Create: `src/Super.Cook/ICooker.cs`, `CookInput.cs`, `CookOutput.cs`, `CookPipeline.cs`,
  `CookedStore.cs`, `Cookers/DataCooker.cs`
- Test: `tests/Super.Cook.Tests/PipelineTests.cs`, `tests/Super.Cook.Tests/ManifestTests.cs`

**Interfaces:**
- Consumes: `Sources.Gather`, `CookSettings.For`, `CookProblem` and `SourceFile` from Task 1.
- Produces:
  - In `Super`: `public enum ContentKind { Data, Texture, Sound, Model }`.
  - In `Super`: `public sealed record ManifestEntry(ContentKind Kind, string Hash, long Size);`.
  - In `Super`: `public sealed class ContentManifest` (public because `ICookedStore.Commit`
    takes it), with `FileName = "content.manifest"`,
    `string Target`, `IReadOnlyDictionary<string, ManifestEntry> Assets` (ordinal),
    `static ContentManifest Empty`, `static ContentManifest Parse(byte[] json, string where)`
    (which throws `ContentException` naming `where`), and `byte[] Serialize()`.
    `Serialize` writes `{"version":1,"target":"…","assets":{name:{"kind":"data","hash":"…","size":n}}}`
    with names sorted ordinally, kinds in camelCase, `\n` newlines, and no BOM.
    `static string ObjectPath(string hash) => $"objects/{hash[..2]}/{hash}"`.
  - `public interface ICooker { string Id { get; } int Version { get; } IReadOnlyList<string> Extensions { get; } void Cook(CookInput input, CookOutput output); }`.
    Extensions are lowercase and include the dot. The longest matching suffix wins.
  - `public sealed class CookInput`, with `string Name`, `byte[] Bytes`,
    `string? FullPath`, `JsonObject Settings`, `string Target`,
    `T SettingsAs<T>() where T : new()` (unknown members are an error;
    `JsonUnmappedMemberHandling.Disallow`, case-insensitive), `byte[] Read(string relative)`
    (relative to the source's folder), `string? Find(string fileName)` (looks beside the
    source, then in every folder under it, and gives back the relative path or null),
    and `void Track(string relative)` (records a file the cooker opened some other way).
    Every `Read`, `Find` hit and `Track` is recorded in `IReadOnlyList<string> Inputs`.
  - `public sealed class CookOutput`, with `void Add(ContentKind kind, byte[] bytes)`,
    `void AddPart(string part, ContentKind kind, byte[] bytes)`,
    `void Error(string message, int? line = null)` and `void Warning(string message, int? line = null)`.
  - `public interface ICookedStore { bool Has(string hash); void Put(string hash, byte[] bytes); byte[] Get(string hash); void Commit(ContentManifest manifest); }`.
    `Commit` writes the manifest last and prunes objects it doesn't name.
  - `public sealed class FolderStore(string folder) : ICookedStore`. It writes objects
    to a temp file and then moves them into place. It writes the manifest the same way,
    with `File.Move(temp, path, overwrite: true)`.
  - `public sealed class MemoryStore : ICookedStore`, with
    `public ContentManifest Manifest { get; }`.
  - `public sealed record CookOptions(string Target = "desktop", string? CacheFolder = null);`.
    A null `CacheFolder` means no cache. The cache comes in Task 4.
  - `public sealed record CookResult(IReadOnlyList<CookProblem> Problems, int Cooked, int FromCache) { public bool Succeeded => !Problems.Any(p => p.IsError); }`
  - `public sealed class CookPipeline(IReadOnlyList<ICooker> cookers, CookOptions options) { public CookResult Cook(IReadOnlyList<ISourceRoot> roots, ICookedStore store); }`.
    It doesn't commit when there are errors, so the store keeps its old manifest. A file
    with no cooker for its extension, unless `cook` is false, is an error:
    `"<name> has no cooker. Add a rule with \"cook\": false if it isn't an asset."`.
  - `public sealed class DataCooker : ICooker`, with id `"data"`, extensions `[".json"]`
    and version `1`. It checks the bytes parse as JSON (with comments and trailing commas
    allowed), and on failure reports the line from `JsonException.LineNumber + 1`. It
    adds the bytes unchanged.
  - `public static class BuiltInCookers { public static IReadOnlyList<ICooker> All { get; } }`.
    For now that's `[new DataCooker()]`, and Task 3 adds more.

- [ ] **Step 1: Write the failing tests**

`ManifestTests`:
- `Serialize_then_parse_round_trips`.
- `Serialize_is_sorted_and_byte_identical_regardless_of_insert_order`.
- `Parse_names_the_file_on_bad_json`. Assert the type is `ContentException` and the
  message contains `where`.

`PipelineTests` (with `MemoryRoot` and `MemoryStore`):
- `Cooks_json_unchanged_with_its_hash`. The entry for `items/axe.json` has
  `Kind == Data`, `Hash == hex(SHA256(bytes))` and
  `store.Get(hash) == bytes`.
- `Bad_json_reports_the_line`. With `"{\n \"a\": 1,\n oops\n}"`, expect an error with
  `Line == 3` and no manifest committed.
- `File_without_cooker_is_an_error_naming_it`. Use `a.xyz`.
- `Cook_false_skips_a_file_without_a_cooker`.
- `Parts_are_named_with_hash`. Use a test-only `ICooker` (defined in the test file) that
  calls `AddPart("a", …)` and `AddPart("b", …)` for `x.part`. The manifest then holds
  `x.part#a` and `x.part#b`, and not `x.part`.
- `Deleted_source_leaves_manifest_and_object_is_pruned` (with a `FolderStore` in a temp
  folder). Cook `a.json` and `b.json`, remove `b.json`, cook again. Then `b.json` isn't
  in the manifest and its object file doesn't exist. Review Focus 2.
- `Renamed_case_takes_the_new_case` (a `FolderRoot` in a temp folder). Cook `fox.json`,
  rename it to `Fox.json`, cook again. The manifest keys are exactly `["Fox.json"]`.
  Review Focus 1.
- `Failed_cook_keeps_the_old_manifest`.
- `Same_sources_give_byte_identical_manifest`. Cook twice into two `MemoryStore`s and
  compare `Serialize()` byte for byte.

- [ ] **Step 2: Run the tests and watch them fail.** Run `dotnet test tests/Super.Cook.Tests`.
  The build fails.
- [ ] **Step 3: Implement the types.** Cook files in parallel if you like, but build the
  manifest from results sorted by name.
- [ ] **Step 4: Run the tests and watch them pass.** Then make `DataCooker` add
  `bytes[1..]` and watch `Cooks_json_unchanged...` fail. Revert.
- [ ] **Step 5: Commit** with `feat: cook roots into a manifest and objects stored by hash`.

---

### Task 3: Texture and sound cookers, and the cooked texture format

**Files:**
- Create: `src/Super/Rendering/CookedTexture.cs`
- Create: `src/Super.Cook/Cookers/TextureCooker.cs`, `src/Super.Cook/Cookers/SoundCooker.cs`
- Modify: `src/Super.Cook/BuiltInCookers.cs` (add both)
- Test: `tests/Super.Cook.Tests/TextureCookerTests.cs`, `tests/Super.Cook.Tests/SoundCookerTests.cs`

**Interfaces:**
- Produces:
  - In `Super`: `internal sealed record CookedTexture(int Width, int Height, TextureKind Kind, bool NormalMap, byte[] Pixels)`,
    with `byte[] Write()`, `static CookedTexture Read(byte[] bytes, string name)` and
    `static (int Width, int Height) ReadSize(byte[] bytes, string name)`. The layout,
    all little-endian, is: `uint` magic `0x58455453` ("STEX"), `int` version `1`,
    `int` width, `int` height, `int` kind, `byte` normalMap, then the RGBA8 pixels
    (`width*height*4` bytes). A bad magic, version or length throws `ContentException`
    with the text `"<name> is not a current cooked texture. Cook again."`.
  - `TextureCooker`, with id `"texture"`, extensions `[".png", ".jpg", ".jpeg", ".tga"]`
    and version `1`. Its settings are `record TextureSettings { bool Linear; bool NormalMap; }`.
    A normal map is always linear. It decodes with `ImageResult.FromMemory(…, RedGreenBlueAlpha)`.
    When a file can't be decoded, the error is `"<name> is not an image this can read"`.
  - `SoundCooker`, with id `"sound"`, extensions `[".wav", ".ogg", ".mp3"]` and version
    `1`. It calls `SoundClip.Read(input.Name, input.Bytes)` to prove the file decodes.
    `ContentException` messages become errors. It adds the bytes unchanged.

- [ ] **Step 1: Write the failing tests**
- `CookedTexture_round_trips`, and `CookedTexture_rejects_a_short_file_naming_it`
  (assert the exact message above).
- `Png_cooks_to_rgba_pixels_in_srgb`. Encode a 2×1 PNG in the test with
  `StbImageWriteSharp`, or write the PNG bytes by hand, with pixels `(255,0,0,255)` and
  `(0,0,255,128)`. Expect `Width == 2`, `Kind == Color`, and those 8 bytes exactly.
- `Linear_setting_gives_linear`. `Normal_map_is_linear_even_without_linear`.
- `Unknown_texture_setting_is_an_error`. With `{ "mipmap": false }`, the message
  contains `mipmap`.
- `Garbage_png_is_an_error_naming_it`.
- `Wav_cooks_unchanged`. Use `TestWav.Make` (copy the helper from
  `tests/Super.Tests/Audio` into `Super.Testing` and make it public, so both test
  projects share it).
- `Broken_ogg_is_an_error_naming_it`.

- [ ] **Step 2: Run the tests and watch them fail.**
- [ ] **Step 3: Implement.** Add `StbImageSharp` to `Super.Cook.csproj`.
- [ ] **Step 4: Run the tests and watch them pass.** Swap the order of the pixel bytes
  and watch `Png_cooks...` fail. Revert.
- [ ] **Step 5: Commit** with `feat: cook textures and sounds`.

---

### Task 4: The local cache, with input tracking

**Files:**
- Create: `src/Super.Cook/CookCache.cs`
- Modify: `src/Super.Cook/CookPipeline.cs`
- Test: `tests/Super.Cook.Tests/CacheTests.cs`

**Interfaces:**
- Consumes: `CookInput.Inputs` and `CookOptions.CacheFolder`.
- Produces:
  - `internal sealed class CookCache(string folder)`.
  - The record key is `SHA256(cookerId \0 version \0 target \0 canonicalSettings \0 sourceHash)`,
    where `canonicalSettings` is the settings JSON with object keys sorted ordinally.
    The record lives at `records/<key>.json` and holds
    `{ "inputs": [{ "name": rel, "hash": h }], "outputs": [{ "part": p|null, "kind": k, "hash": h }], "warnings": [...] }`.
  - Outputs are stored in `objects/<hash[..2]>/<hash>` under the cache folder.
  - A hit means the record exists, every input still hashes the same, and every output
    object exists. On a hit, the outputs are copied into the store (`Put` when `!Has`)
    and `FromCache` goes up by one.
  - Results with errors are never cached.
  - Source hashes are remembered in `hashes.json`, keyed by full path, size and
    last-write time ticks, so an unchanged file isn't hashed again. Memory roots always
    hash.
  - `public static string CookOptions.DefaultCacheFolder`. That's `SUPER_CACHE`, or
    `~/.super/cache` when it isn't set.

- [ ] **Step 1: Write the failing tests** (temp folders for the roots and the cache):
- `Second_cook_comes_from_cache`. The second `CookResult` has `Cooked == 0` and
  `FromCache == 1`.
- `Changing_settings_cooks_again`. Add a sidecar `{ "linear": true }` to a PNG and
  expect `Cooked == 1`.
- `Changing_a_tracked_input_cooks_again`. Use a test cooker that calls
  `input.Read("extra.txt")`. Changing `extra.txt` gives `Cooked == 1`, and leaving it
  alone gives `FromCache == 1`.
- `Bumping_cooker_version_cooks_again`.
- `Errors_are_not_cached`. Bad JSON cooked twice gives `Cooked == 1` both times.
- `Cache_is_shared_across_output_folders`. Cook into store A, then into an empty store
  B with the same cache, and expect `FromCache == 1` and B's object present.

- [ ] **Step 2: Run the tests and watch them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the tests and watch them pass.** Leave the input hashes out of the
  hit check and watch `Changing_a_tracked_input...` fail. Revert.
- [ ] **Step 5: Commit** with `feat: cache cooked assets by the hash of everything they read`.

---

### Task 5: The model cooker

**Files:**
- Create: `src/Super.Cook.Models/Super.Cook.Models.csproj` (references `Super.Cook`,
  `Rendition`, `Silk.NET.Assimp` 2.23.0 and `StbImageSharp`; add it to `Super.slnx`)
- Move: `tools/Rendition.Import/{ImportSettings,RigImporter,TextureCooker}.cs` →
  `src/Super.Cook.Models/`. Rename `TextureCooker` to `ModelTextures` so it doesn't
  clash with Task 3.
- Create: `src/Super.Cook.Models/ModelCooker.cs`, made from the body of `Program.cs`
- Delete: `tools/Rendition.Import/` (and remove it from `Super.slnx`)
- Modify: `src/Rendition/ModelData.cs`. Add `public byte[] ToBytes()` and
  `public static ModelData Read(Stream stream, string name)` and
  `public static (Skeleton?, AnimationClip[]) ReadRig(Stream stream, string name)`. The
  path overloads call these.
- Test: `tests/Super.Cook.Tests/ModelCookerTests.cs` and `tests/Super.Cook.Tests/TinyGltf.cs`

**Interfaces:**
- Consumes: `CookInput` (including `FullPath`, `Find` and `Track`) and `CookOutput.Add`/`AddPart`.
- Produces:
  - `public sealed class ModelCooker : ICooker`, with id `"model"`, extensions
    `[".glb", ".gltf", ".fbx", ".obj"]` and version `1`.
  - The settings are `ImportSettings` plus `Dictionary<string, ImportSettings>? Parts`.
    When `Parts` is set, the cooker adds each part with `AddPart(key, Model, bytes)` and
    doesn't add the whole file. A source with no `FullPath` gives the error
    `"<name>: models can only be cooked from files on disk"`.
  - `ModelTextures` looks textures up through `input.Find` and `input.Read`, never
    through `File` or `Directory`.
  - The cooker calls `input.Track` on each `buffers[].uri` of a `.gltf` (parse the JSON)
    and on each animation file in `settings.Animations`.
  - Failures that used to print and return 1 become `output.Error(...)` calls with the
    same text.
  - `ModelData.ToBytes()` writes the same bytes `Write(path)` writes today, so cooked
    `.rmodel` data is unchanged.

- [ ] **Step 1: Write the failing tests.** `TinyGltf.Write(folder, name, materialName, nodeNames)`
  writes a `.gltf` with an embedded base64 buffer, holding one triangle per node name.
- `Gltf_cooks_to_rmodel_that_reads_back`. `ModelData.Read(new MemoryStream(bytes), name)`
  gives 3 vertices per node.
- `Parts_cook_each_node_by_name`. With nodes `fir_tall` and `fir_young` and a sidecar
  `parts: { tall: { node: "fir_tall" }, young: { node: "fir_young" } }`, the manifest
  holds `firs.gltf#tall` and `firs.gltf#young`, and each has 3 vertices.
- `Unknown_model_setting_is_an_error`. Use `{ "targetLenght": 2 }`.
- `Missing_texture_named_in_settings_is_an_error_naming_it`.
- `Changing_the_gltf_buffer_cooks_again`. Use an external `.bin` and the cache from
  Task 4.
- `Memory_source_model_is_an_error`.

- [ ] **Step 2: Run the tests and watch them fail.**
- [ ] **Step 3: Implement.** Keep the importer's logic as it is. Change only where files
  come from and where errors go.
- [ ] **Step 4: Run the tests and watch them pass.** Make `ModelTextures` skip
  `input.Read` and read the file directly, and watch the cache test fail. Revert.
- [ ] **Step 5: Run the existing tests.** `dotnet test tests/Super.Tests --filter FullyQualifiedName~Clip`
  should still pass, because `ModelData` behaves the same.
- [ ] **Step 6: Commit** with `feat: cook models through the pipeline, with parts`.

---

### Task 6: The `super-cook` command, watch mode and MSBuild targets

**Files:**
- Create: `tools/Super.Cook.Tool/Super.Cook.Tool.csproj` (Exe, `AssemblyName` `super-cook`,
  referencing `Super.Cook` and `Super.Cook.Models`; add it to `Super.slnx`)
- Create: `tools/Super.Cook.Tool/Program.cs`
- Create: `src/Super.Cook/CookWatcher.cs`
- Create: `src/Super.Build/Super.targets`
- Create: `src/Super/Data/DevSettings.cs`
- Test: `tests/Super.Cook.Tests/WatcherTests.cs` (in a `DisableParallelization` collection, with `[Trait("Speed","Slow")]`)
- Docs: `docs/command-line.md`, a `super-cook` section

**Interfaces:**
- Produces:
  - The CLI is `super-cook --content <dir> [--content <dir> ...] --out <dir> [--target desktop] [--cache <dir> | --no-cache] [--dev-file <path>] [--watch]`.
    The exit code is 0 on success and 1 on any error. Each problem prints as one
    `CookProblem.ToString()` line on stdout. The last line is
    `Cooked <n>, from cache <m>, <e> errors.`
  - `super-cook file --content <dir> <name> <output path>` cooks one asset (or one
    `#part`) and writes its bytes. It's for `Rendition.Sample`.
  - In `Super`: `public sealed record DevSettings(string[] Roots, string Tool, string Target)`,
    with `static DevSettings? Read(string path)` and `void Write(string path)`. That's
    `content.dev.json`: `{ "roots": [...], "tool": "<full path of super-cook.dll>", "target": "desktop" }`.
    `--dev-file` writes it, and `Tool` is `typeof(Program).Assembly.Location`.
  - `public sealed class CookWatcher(CookPipeline pipeline, IReadOnlyList<FolderRoot> roots, ICookedStore store, TextWriter log, TimeSpan quiet, TimeProvider? clock = null) : IDisposable`.
    It watches every root, including subfolders. After `quiet` with no further changes
    it cooks. Problems go to `log`, and a failed cook keeps the old manifest. An
    `IOException` while cooking schedules another cook after `quiet`. It writes `"Cooked."`
    or `"Not cooked: <n> errors."` to `log` after each attempt.
  - `Super.targets`:
    - `SuperTarget` defaults to `desktop`.
    - If there are no `@(SuperContent)` items and `content` exists, `content` is the root.
    - Target `SuperCook`, `AfterTargets="Build"`, skipped when there are no roots. It
      gets the tool's path through `<MSBuild Projects="…/Super.Cook.Tool.csproj" Targets="GetTargetPath">`,
      after building that project with `<MSBuild Projects=… Targets="Build" />`. Then it
      runs `<Exec Command="dotnet &quot;$(tool)&quot; @(roots->'--content &quot;%(FullPath)&quot;', ' ') --out &quot;$(OutDir)content&quot; --target $(SuperTarget) --dev-file &quot;$(OutDir)content.dev.json&quot;" />`.
      The canonical lines become build errors.
    - Target `SuperPublishContent`, `AfterTargets="Publish"`, copies
      `$(OutDir)content\**` to `$(PublishDir)content\`, and never copies `content.dev.json`.

- [ ] **Step 1: Write the failing watcher tests** (temp roots, a `FolderStore`, a 50 ms
  quiet period, and polling for at most 10 s, never a fixed sleep):
- `Saving_a_file_recooks_it`. Change `items/axe.json` and poll until the manifest hash
  for it changes.
- `Bad_save_keeps_old_manifest_and_logs`. Write invalid JSON, poll until the log
  contains `"Not cooked"`, then assert the manifest is unchanged.
- `Locked_file_retries_after_quiet`. Hold `File.Open(path, FileShare.None)` on a changed
  file, poll for the retry in the log, release it, then poll until the manifest
  updates. Review Focus 3.

- [ ] **Step 2: Run the tests and watch them fail.**
- [ ] **Step 3: Implement `CookWatcher`, `DevSettings`, the CLI and `Super.targets`.**
  The CLI's watch mode is one cook, then a `CookWatcher` running until stdin closes or
  the process is killed.
- [ ] **Step 4: Run the tests and watch them pass.** Remove the `IOException` retry and
  watch `Locked_file...` fail. Revert.
- [ ] **Step 5: Smoke-test the build.** Temporarily import `Super.targets` in
  `examples/Crafting/Crafting.csproj` and run `dotnet build examples/Crafting`. Expect
  `examples/Crafting/bin/Debug/net10.0/content/content.manifest`, listing
  `items/axe.json`, and `content.dev.json`. Break a JSON file and expect the build to fail
  with `items/<file>.json(<line>): error SUPER001`. Revert both. Task 9 does the real
  migration.
- [ ] **Step 6: Document `super-cook` in `docs/command-line.md`**, with every flag above
  and the `file` command.
- [ ] **Step 7: Commit** with `feat: super-cook command, watch mode and build targets`.

---

### Task 7: The runtime reads only cooked content

**Files:**
- Modify: `src/Super/Data/ContentFiles.cs`. Change `IContentFiles` and replace
  `ContentFolder` with `CookedFolder`.
- Modify: `src/Super/Content.cs`, `src/Super/Data/ContentReader.cs`,
  `src/Super/Data/ContentValidator.cs`, `src/Super/Rendering/SceneRenderer.cs`,
  `src/Super/Ui/Image.cs`, `src/Super/GameHost.cs`
- Modify: `src/Super.Testing/MemoryContent.cs` and `src/Super.Testing/Super.Testing.csproj`
  (reference `Super.Cook`)
- Modify: the tests in `tests/Super.Tests` that use `ContentFolder`, raw `MemoryContent`
  models or `Texture(..., TextureKind, normalMap)`
- Docs: `docs/content.md` (cooking, names, rules, sidecars, parts, shipping, tests) and
  the content part of `docs/design.md`

**Interfaces:**
- Consumes: `ContentManifest`, `CookedTexture`, `ContentKind`, `CookPipeline`,
  `MemoryRoot`, `MemoryStore` and `BuiltInCookers`.
- Produces:
  - `public interface IContentFiles`, with `ContentKind? KindOf(string name)`,
    `IReadOnlyList<string>? List(string folder)` (data names under the folder, ordinal,
    or null when there are none), `byte[] Read(string name)` (cooked bytes), and
    `void Refresh() { }`.
  - `public sealed class CookedFolder(string folder) : IContentFiles`. With no manifest
    it holds `ContentManifest.Empty`. It has `static CookedFolder BesideProgram()`,
    which is `AppContext.BaseDirectory/content`, and `internal DevSettings? Dev`, read
    from `<folder>/../content.dev.json`. `Refresh()` reads the manifest again.
  - `Content.Model`, `Rig`, `Image`, `Texture(string)`, `Sound` and `Ambience` check
    `KindOf(name)` when the asset is named. When the name is missing:
    `"<name> isn't in the cooked content. Check the name, including its case and extension."`.
    When the name holds `#` and its base has parts, the message ends
    `" <base> has the parts: a, b."`, listing the manifest keys that start with `base#`.
    When it's the wrong kind: `"<name> is a <kind>, not a <wanted>."`.
  - `Content.Texture(string path)` loses `kind` and `normalMap`. The
    `Texture(GeneratedTexture, ...)` overload keeps them. `TextureItem` becomes
    `TextureItem(string? Name, GeneratedTexture? Generated, IContentFiles Files, TextureKind Kind, bool NormalMap)`.
    For a named texture, `Kind` and `NormalMap` are read from the cooked header in
    `SceneRenderer.LoadTexture`. The deduplication key is the name alone.
  - `GameImage` holds the cooked bytes. `LoadImage` uses `CookedTexture.Read`, and the
    image's width and height come from `CookedTexture.ReadSize` when it's named.
  - `Rig` and `PrepareItem` read models with `ModelData.ReadRig(new MemoryStream(files.Read(name)), name)`
    and `ModelData.Read(...)`. `ContentFolder.FindCooked` and the upward search are
    removed.
  - `ContentValidator` checks model references with `files.KindOf(path) == ContentKind.Model`,
    using the missing-name message above.
  - `MemoryContent : IContentFiles` keeps its indexer, `Add` and `Remove`, which now set
    **sources** in a `MemoryRoot`. On the first read after a change, it cooks with
    `BuiltInCookers.All` into a `MemoryStore`, and throws `ContentException` listing the
    problems when the cook fails. `AddModel(name)` adds a manifest entry of kind `Model`
    with empty bytes. The "every model exists until one is added" behaviour is removed.
  - `GameHost` registers `IContentFiles` as `CookedFolder.BesideProgram()`.

- [ ] **Step 1: Write the failing tests** in `tests/Super.Tests/Data/CookedContentTests.cs`:
- `Naming_a_missing_asset_explains`. `content.Model("models/fox.glb")` with an empty
  `MemoryContent` throws `ContentException`, and the message equals the missing-name
  text exactly.
- `Wrong_case_is_missing`. With `AddModel("fox.glb")`, `Model("Fox.glb")` throws.
- `Unlisted_part_lists_the_parts`. With `AddModel("firs.glb#tall")` and
  `AddModel("firs.glb#young")`, `Model("firs.glb#tal")` gives a message ending
  `"firs.glb has the parts: tall, young."`. Review Focus 4.
- `Wrong_kind_explains`. `Model("items/axe.json")` gives `"items/axe.json is a data, not a model."`.
  (Write the kind names in lowercase.)
- `Image_size_comes_from_the_cooked_header`. Add a 3×2 PNG through `MemoryContent.Add`
  and expect `Width == 3` and `Height == 2`.
- `Normal_map_kind_comes_from_cook_settings`. Add `cook.json` with
  `{rules:[{match:"*-normal.png", texture:{normalMap:true}}]}`. Then
  `SceneRenderer.LoadTexture` for `a-normal.png` gives `Kind == Linear`.
- `CookedFolder_without_manifest_is_empty`.
- `CookedFolder_reads_what_FolderStore_wrote`. Cook a temp root with `CookPipeline` into
  a `FolderStore`. Then `new Content(new CookedFolder(dir)).LoadAll<Item>("items")` reads
  the record.

- [ ] **Step 2: Run the tests and watch them fail.** Run `dotnet test tests/Super.Tests --filter FullyQualifiedName~CookedContent`.
- [ ] **Step 3: Implement the runtime changes.**
- [ ] **Step 4: Fix the existing tests.** Run `dotnet test tests/Super.Tests`. Tests that
  call `content.Model("a")` with no content get `files.AddModel("a")`. `LiveReloadTests`,
  `LoadAllTests` and `SchemaTests`, which build `new ContentFolder(root)` over raw JSON,
  now cook the temp root with `CookPipeline` into a `FolderStore` and use `CookedFolder`.
  The `IsSource` tests are deleted, because Task 8 replaces them with tests of
  `content.dev.json`. Tests that pass `TextureKind` to `Texture(string, ...)` use a
  `cook.json` rule instead.
- [ ] **Step 5: Run every test project and watch them pass.** Run `dotnet test Super.slnx`.
  Example test projects that link raw JSON (`Rooms.Tests` and any others) fail until
  Task 9. Note which ones fail, and don't skip them.
- [ ] **Step 6: Make sure the tests can fail.** Drop the `#` part suffix from the message
  and watch `Unlisted_part...` fail. Revert.
- [ ] **Step 7: Update `docs/content.md` and `design.md`.** Grep `docs/` for
  `assets/cooked`, `ContentFolder`, `HasModel`, `TextureKind.Linear, normalMap` and
  `CopyToPublishDirectory`, and fix every hit.
- [ ] **Step 8: Commit** with `feat: games read only cooked content`.

---

### Task 8: JSON live reload through watch mode

**Files:**
- Modify: `src/Super/Content.cs` (`GoLive`, `StopLive`, `Reload`) and
  `src/Super/Data/ContentWatcher.cs` (take a file filter, and drop the `.schemas` rule)
- Create: `src/Super/Data/CookProcess.cs`
- Test: `tests/Super.Tests/Data/LiveReloadTests.cs` (update it)
- Docs: the "Change it while the game runs" section of `docs/content.md`

**Interfaces:**
- Consumes: `CookedFolder.Dev` and `DevSettings`.
- Produces:
  - `internal interface ICookProcess : IDisposable`.
  - `internal sealed class CookProcess(DevSettings dev, string outFolder) : ICookProcess`.
    It starts `dotnet "<tool>" --watch --content … --out "<outFolder>" --target <target>`,
    with stdout and stderr copied to `Console.Error`, and calls `Kill(entireProcessTree: true)`
    in `Dispose`.
  - `Content` gets an internal seam, `Func<DevSettings, string, ICookProcess> StartCook`,
    for tests.
  - `GoLive()`: when `_files is CookedFolder { Dev: { } dev }`, it writes the schemas
    into `dev.Roots[0]`, starts the cook process, and watches `content.manifest` in the
    cooked folder with `ContentWatcher`. When the manifest changes it sets `ReloadWanted`.
  - `Reload` calls `_files.Refresh()` before it reads the catalogs again.
  - Live mode stays off where it's off today: headless runs, `--screenshot`, `--record`,
    `--profile`, and wherever there's no `content.dev.json`, which includes published games.

- [ ] **Step 1: Write the failing tests.**
- `GoLive_starts_cook_with_dev_roots` (with a fake `StartCook`). Its arguments are the
  roots and the cooked folder.
- `No_dev_file_means_no_cook_and_no_watch`.
- `Manifest_change_reloads_catalog`. This one is Slow and in `DisableParallelization`.
  Use a temp cooked folder from `FolderStore` and a fake `StartCook`. Cook a changed
  `items/axe.json` into the same store, poll the `HeadlessGame` ticks until
  `items["axe"]` shows the new value, and check that `OnChange` fired.
- `StopLive_disposes_the_cook`.

- [ ] **Step 2: Run the tests and watch them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the tests and watch them pass.** Remove `Refresh()` from `Reload`
  and watch `Manifest_change...` fail. Revert.
- [ ] **Step 5: Update the live reload section of `docs/content.md`.** It covers watch
  mode, `content.dev.json`, and the old data staying when a save is bad.
- [ ] **Step 6: Commit** with `feat: live reload through super-cook watch mode`.

---

### Task 9: The shared `assets/` folder and moving the examples over

**Files:**
- Move (git): `assets/<x>.materials.json` → sidecars (see the table)
- Create: `assets/cook.json` and `assets/README.md` (where each source comes from and the
  name it's saved under, taken from the "Getting the assets" sections of the example
  READMEs, which then link here)
- Modify: `.gitignore`
- Modify: every example `.csproj` that has content or models, and `Crafting.Tests`,
  `Forest.Tests`, `Reef.Tests` and `Rooms.Tests` `.csproj`
- Modify: `examples/Archer/{ArcherGame,Clearing}.cs`, `examples/Fox/*.cs`,
  `examples/Reef/*.cs` and the Reef JSON or code that names models
- Docs: `docs/rendering.md#models`, `docs/animation.md`, `docs/getting-started.md`,
  `examples/Archer/README.md`, `src/Rendition/README.md`, `CREDITS.md` paths

**Interfaces:**
- Consumes: `Super.targets` (Task 6), sidecar and part settings (Task 5), and
  `super-cook file` (Task 6, for `Rendition.Sample`).

The new layout of the local files (move them on disk: `assets/source/<x>` → `assets/<x>`,
and delete `assets/cooked`):

| Old cooked name | New name | Sidecar (from) |
|---|---|---|
| `archer.rmodel` | `archer/erika.fbx` | `archer/erika.fbx.cook.json` (`archer.materials.json`, with animation files under `animations/`) |
| `fox.rmodel` | `fox/fox.glb` (renamed from `Fox.glb`) | `fox/fox.glb.cook.json` (`fox`) |
| `props/dummy.rmodel` | `archer-props/dummy.glb` | `prop-dummy` |
| `props/arrow.rmodel` | `archer-props/arrow.glb` | `prop-arrow` |
| `props/fir-{tall,broad,young}.rmodel` | `archer-props/firs.glb#{tall,broad,young}` | `parts` from `prop-fir-*` |
| `props/fern.rmodel` | `props/fern_02/fern_02.gltf` | `prop-fern` |
| `props/shrub.rmodel` | `props/shrub_02/shrub_02.gltf` | `prop-shrub` |
| `props/nettle.rmodel` | `props/nettle_plant/nettle_plant_1k.gltf` | `prop-nettle` |
| `props/branches.rmodel` | `props/dry_branches_medium_01/dry_branches_medium_01_1k.gltf` | `prop-branches` |
| `props/grass-{tall,large}.rmodel` | `props/grass_medium_01/grass_medium_01.gltf#{tall,large}` | `parts` |
| `props/dandelion-{a,b}.rmodel` | `props/dandelion_01/dandelion_01_1k.gltf#{a,b}` | `parts` |
| `props/periwinkle-{a,b}.rmodel` | `props/periwinkle_plant/periwinkle_plant_1k.gltf#{a,b}` | `parts` |
| `props/mossy-rock-{a,b}.rmodel` | `props/rock_moss_set_01/rock_moss_set_01_1k.gltf#{a,b}` | `parts` |
| `props/log.rmodel`, `stump`, `stump-b` | `props/dead_tree_trunk/dead_tree_trunk.gltf`, `props/tree_stump_01/tree_stump_01.gltf`, `props/tree_stump_02/tree_stump_02_1k.gltf` | none needed (`prop-solid` was empty) |
| `reef/coral.rmodel` | `reef/coral/coral.glb` | `reef-coral` |
| `reef/softcoral.rmodel` | `reef/softcoral/softcoral.fbx` (renamed from `Corals.fbx`) | `reef-softcoral` |
| `reef/tilapia.rmodel` | `reef/tilapia/tilapia.glb` (renamed) | `reef-tilapia` |
| `reef/tuna.rmodel` | `reef/tuna/tuna.fbx` (renamed from `TUNAFBX.fbx`) | `reef-tuna` |
| `adventurer.rmodel`, `ford-gt.rmodel` | `adventurer/adventurer.glb`, `ford-gt/ford-gt.fbx` (renamed) | `adventurer`, `ford-gt`. For `Rendition.Sample`, through `super-cook file` |

Cooked models no example names (`props/boulder`, `fir-a`, `fir-b`) are dropped. Grep
first to confirm nothing names them.

`assets/cook.json`:

```json
{
  "rules": [
    { "match": "**/textures/**", "cook": false },
    { "match": "archer/animations/**", "cook": false },
    { "match": "archer-props/*.png", "cook": false },
    { "match": "ford-gt/*.png", "cook": false },
    { "match": "reef/**/*.png", "cook": false }
  ]
}
```

`.gitignore` replaces `assets/source/` and `assets/cooked/` with:

```
assets/**
!assets/**/
!assets/cook.json
!assets/**/*.cook.json
!assets/README.md
```

- [ ] **Step 1: Turn each `assets/*.materials.json` into its sidecar** with `git mv`, and
  merge the variant files into `parts`. `git status` should show the sidecars as added
  files that aren't ignored.
- [ ] **Step 2: Move the local source files on disk** to the new layout and delete
  `assets/cooked`.
- [ ] **Step 3: Import `Super.targets` in each project.**
  - Archer, Fox and Reef list `..\..\assets`, plus their own `content` where it exists.
  - Crafting, Forest and Rooms list their own `content`.
  - Remove the old `<Content Include=…>` publish items.
  - Archer gets `examples/Archer/content/cook.json` with
    `{ "match": "textures/*-normal.*", "texture": { "normalMap": true } }`, and
    `Clearing.cs` drops `TextureKind.Linear, normalMap: true`.
  - Each example test project imports `Super.targets` with its example's roots, in
    place of the linked `<None Include=…content…>`.
- [ ] **Step 4: Rename every `content.Model(…)` and `content.Rig(…)` in the examples**
  to the names in the table.
- [ ] **Step 5: Build and test everything.** Run `dotnet build Super.slnx`, then
  `dotnet test Super.slnx`. Expect both to succeed. On a machine without the licensed
  sources, Archer, Fox and Reef still build, and their missing assets show up only when
  the game runs (`ContentException`).
- [ ] **Step 6: Check each example visually** with the renderer's own frames, never a
  desktop capture:
  `dotnet run -c Release --project examples/Archer -- --screenshot out/archer.png`,
  and the same for Fox and Reef. Compare them with screenshots taken on `main` before
  the change. They should match, because the cooked model bytes are unchanged.
- [ ] **Step 7: Check that live reload still works.** Run
  `dotnet run --project examples/Crafting`, edit `content/items/axe.json`, and see
  "Content reloaded." printed.
- [ ] **Step 8: Update the docs listed above.** `rendering.md#models` describes
  sidecars, rules and parts in place of the import command. The README tables use the
  new names.
- [ ] **Step 9: Commit** with `feat: shared assets folder, examples cook through the pipeline`.

---

### Task 10: Docs sweep

**Files:** `docs/**`, `README.md`, `src/Rendition/README.md`, `examples/*/README.md`

- [ ] **Step 1: Search for leftovers.** Run `grep -rn "Rendition.Import\|assets/cooked\|assets/source\|materials.json\|\.rmodel\"\|ContentFolder\|HasModel" docs README.md src/*/README.md examples/*/README.md`.
  Fix every hit, apart from historical specs and plans under `docs/superpowers`.
- [ ] **Step 2: Update `docs/design.md`.** The "Model import" table row becomes "cooked
  by `Super.Cook.Models` through the pipeline". Add a short "Asset pipeline" section on
  the pure-function rule, the cache key, the manifest and objects stored by hash, and
  `content.dev.json`.
- [ ] **Step 3: Update `docs/roadmap.md`.** Asset pipeline step 1 is done, and step 2
  (hot reload of every asset kind) is next.
- [ ] **Step 4: Commit** with `docs: asset pipeline across the guides`.
