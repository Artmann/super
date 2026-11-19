using Microsoft.Extensions.DependencyInjection;
using Reef;
using Super;

Game.StartWindowsGameLoop<ReefGame>(
    new GameOptions { Title = "Reef", Width = 1600, Height = 900 },
    services =>
    {
        services.AddSingleton<ReefSettings>();
    });
