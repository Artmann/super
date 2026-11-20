using Super;
using WaxExample;

// --plain draws every twin without subsurface, for comparing pictures.
WaxTable.Plain = args.Contains("--plain");
Game.Run<WaxTable>(new GameOptions { Title = "Wax" });
