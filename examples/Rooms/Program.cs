using Microsoft.Extensions.DependencyInjection;
using Rooms;
using Super;
using Super.Ecs;

Game.StartWindowsGameLoop<RoomsGame>(
    new GameOptions { Title = "Rooms", Width = 1280, Height = 720 },
    services => services.AddSuperEcs());
