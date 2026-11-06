using Super;
using GlassExample;

// --flat draws all the glass without bending or frosting, for comparing pictures.
GlassRoom.Flat = args.Contains("--flat");
Game.StartWindowsGameLoop<GlassRoom>(new GameOptions { Title = "Glass" });
