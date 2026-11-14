# Package check

A small game and its tests that use Super the way a game outside this repository does: from the
package, with no project references. `.github/scripts/test-package.ps1` copies this folder
somewhere outside the repository and builds it against a `.nupkg`, so nothing here is in
`Super.slnx`.

The game loads a JSON file, so the build has to cook its content, and uses a shader of its own,
so the build has to compile it with the package's includes.
