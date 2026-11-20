using SkylarkExample;
using Super;

Game.StartWindowsGameLoop<SkylarkGame>(
    new GameOptions { Title = "Skylark", Id = "skylark", Width = 1280, Height = 720 },
    services => services.AddSkylark());
