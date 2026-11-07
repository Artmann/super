using Super;
using Courtyard;

// --lineup shows the material graphs one by one instead of the courtyard.
if (args.Contains("--lineup"))
{
    Game.StartWindowsGameLoop<LineupGame>(new GameOptions { Title = "Courtyard lineup" });
}
else
{
    Game.StartWindowsGameLoop<CourtyardGame>(new GameOptions { Title = "Courtyard" });
}
