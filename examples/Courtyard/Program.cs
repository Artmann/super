using Super;
using Courtyard;

// --lineup shows the material graphs one by one instead of the courtyard, and --leaves the leaf, blade and cobweb
// cards.
if (args.Contains("--lineup"))
{
    Game.Run<LineupGame>(new GameOptions { Title = "Courtyard lineup" });
}
else if (args.Contains("--leaves"))
{
    Game.Run<LeavesGame>(new GameOptions { Title = "Courtyard leaves" });
}
else
{
    Game.Run<CourtyardGame>(new GameOptions { Title = "Courtyard" });
}
