using System.Numerics;
using Super.Testing;

namespace Super.Tests;

public class OnRigTests
{
    private const float Tolerance = 0.0001f;

    private readonly World _world = new();
    private readonly View _view = new();

    // No lag, so the camera is always eight behind and five above where it was last told to look.
    private readonly FollowCamera _follow = new() { Offset = new Vector3(0f, 5f, 8f), Lag = 0f };

    private static Vector3 At(float x) => new(x, 0f, 0f);

    private Camera Shot(float blend = 0f)
    {
        _view.Begin(blend);
        WorldFrame.Describe(_view, _world);
        return _view.Shot;
    }

    private sealed class Follower(Scenes scenes) : IGame
    {
        public FollowCamera Follow { get; } = new() { Offset = new Vector3(0f, 5f, 8f), Lag = 0f };

        public void Load() => scenes.Go(scene =>
        {
            Follow.Update(new Vector3(7f, 0f, 0f), 0f);
            scene.Spawn(new OnRig(Follow));
        });

        public void Update(GameTime time)
        {
            if (time.Tick >= 1)
            {
                Follow.Update(new Vector3(7f + time.Tick, 0f, 0f), time.TickSeconds);
            }
        }
    }

    [Fact]
    public void An_entity_on_a_rig_shows_the_rigs_camera()
    {
        _follow.Update(At(3f), 0.5f);
        _world.Spawn(new OnRig(_follow));

        WorldRigs.Show(_world);

        var shot = Shot();
        Assert.Equal(new Vector3(3f, 5f, 8f), shot.Position);
        Assert.Equal(At(3f), shot.Target);
    }

    [Fact]
    public void A_rig_that_moves_is_blended_between_ticks()
    {
        _follow.Update(At(0f), 0.5f);
        _world.Spawn(new OnRig(_follow));
        WorldRigs.Show(_world);

        LastTicks.Remember(_world);
        _follow.Update(At(4f), 0.5f);
        WorldRigs.Show(_world);

        Assert.Equal(1f, Shot(0.25f).Position.X, Tolerance);
    }

    [Fact]
    public void A_rig_that_jumped_cuts()
    {
        _follow.Update(At(0f), 0.5f);
        _world.Spawn(new OnRig(_follow));
        WorldRigs.Show(_world);

        LastTicks.Remember(_world);
        _follow.Jump();
        _follow.Update(At(4f), 0.5f);
        WorldRigs.Show(_world);

        Assert.Equal(4f, Shot(0.25f).Position.X, Tolerance);
    }

    [Fact]
    public void A_rig_first_updated_after_its_entity_was_spawned_cuts_to_its_place()
    {
        _world.Spawn(new OnRig(_follow));
        WorldRigs.Show(_world);
        Assert.Equal(new Vector3(0f, 5f, 8f), Shot().Position);

        LastTicks.Remember(_world);
        _follow.Update(At(4f), 0.5f);
        WorldRigs.Show(_world);

        Assert.Equal(4f, Shot(0.25f).Position.X, Tolerance);
    }

    [Fact]
    public void The_game_loop_shows_a_rig_built_with_a_scene_and_follows_it()
    {
        using var run = HeadlessGame.Create<Follower>(seed: 1);

        // The scene is built at the end of the first tick, and the camera is on its rig straight away.
        run.Ticks(1);
        Assert.Equal(new Vector3(7f, 5f, 8f), run.World.ActiveCamera()!.Value.Position);

        // Tick 1 moves the rig to 8.
        run.Ticks(1);
        Assert.Equal(new Vector3(8f, 5f, 8f), run.World.ActiveCamera()!.Value.Position);
    }
}
