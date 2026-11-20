using SkylarkExample;
using Super;

Game.Run<SkylarkGame>(
    new GameOptions { Title = "Skylark", Id = "skylark", Width = 1280, Height = 720 },
    services => services.AddSkylark());
