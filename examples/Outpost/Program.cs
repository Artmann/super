using OutpostExample;
using Super;

Game.StartWindowsGameLoop<OutpostGame>(
    new GameOptions { Title = "Outpost", Width = 1280, Height = 720 },
    services => services.AddOutpost());
