using Microsoft.Extensions.DependencyInjection;
using Forest;
using Super;
using Super.Ecs;

Game.StartWindowsGameLoop<ForestGame>(
    new GameOptions { Title = "Forest", Width = 1280, Height = 720 },
    services => services.AddSuperEcs());
