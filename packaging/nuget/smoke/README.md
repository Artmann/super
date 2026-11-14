# Package check

A small game and its tests that use Super the way a game outside this repository does: from the
package, with no project references. `.github/scripts/test-package.ps1` copies this folder
somewhere outside the repository and builds it against a `.nupkg`, so nothing here is in
`Super.slnx`.

The game loads a JSON file and a model, so the build has to cook its content with the package's
cook tool, including Assimp, the native library that tool imports models with. It uses a shader of
its own, so the build has to compile it with the package's includes. The script also checks that
every Windows native library the cook tool's `super-cook.deps.json` names is at that path in the
package.
