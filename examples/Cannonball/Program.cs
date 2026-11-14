using Cannonball;
using Super;
using Super.Physics;

Game.StartWindowsGameLoop<CannonballGame>(
    new GameOptions { Title = "Human Cannonball", Width = 1600, Height = 900, TicksPerSecond = 60 },
    services => services.AddSuperPhysics());
