## Code Style 

- Always use bracers for control statements like if and while.

## Seeing the game

To see a game, use `--screenshot` or `--record` from the README, or `GameCapture`
from code. They use the renderer's own frames, so they stay clean and repeatable.
Never capture the desktop: that can pick up the user's other windows.

## Commits

Use conventional commits (`feat:`, `fix:`, `docs:`, `chore:`). Don't list Claude
as an author and don't add Co-Authored-By lines.
