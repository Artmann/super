using System.Numerics;
using Rendition;

namespace Super.Tests;

public class PlacedTests
{
    private readonly World _world = new();
    private readonly View _view = new();

    private void Describe()
    {
        _view.Begin(0f);
        WorldFrame.Describe(_view, _world);
    }

    [Fact]
    public void A_decal_takes_its_box_from_the_entity()
    {
        var box = new Transform(new Vector3(2f, 0f, -1f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), new Vector3(2f, 0.5f, 3f));
        var stain = _world.Spawn(box, new Decal(new Surface()));

        Describe();

        var decal = Assert.Single(_view.Effects.DrawnDecals.ToArray());
        Assert.Equal(box.Matrix, decal.Box);
        Assert.Equal(stain.Id, decal.Key);
    }

    [Fact]
    public void A_fog_volume_follows_its_parent()
    {
        var room = _world.Spawn(Transform.At(new Vector3(10f, 0f, 0f)));
        _world.Spawn(Transform.At(new Vector3(0f, 1f, 0f), 4f), new Parent(room), new FogVolume(FogShape.Box, new FogVolumeSettings(0.2f)));

        Describe();

        var fog = Assert.Single(_view.Effects.DrawnFogVolumes.ToArray());
        Assert.Equal(new Vector3(10f, 1f, 0f), fog.Transform.Translation);
        Assert.Equal(4f, fog.Transform.M11);
        Assert.Equal(FogShape.Box, fog.Shape);
        Assert.Equal(0.2f, fog.Settings.Density);
    }

    [Fact]
    public void A_reflection_capture_is_photographed_from_the_entity()
    {
        var capture = _world.Spawn(Transform.At(new Vector3(3f, 1f, 2f)),
            new ReflectionCapture(new Vector3(-1f), new Vector3(2f, 3f, 2f), Blend: 0.25f, Priority: 2f));

        Describe();

        var probe = Assert.Single(_view.Indirect.ReflectionProbes.ToArray());
        Assert.Equal(capture.Id, probe.Key);
        Assert.Equal(new Vector3(3f, 1f, 2f), probe.Position);
        Assert.Equal(new Vector3(2f, 0f, 1f), probe.Min);
        Assert.Equal(new Vector3(5f, 4f, 4f), probe.Max);
        Assert.Equal(0.25f, probe.Blend);
        Assert.Equal(2f, probe.Priority);
    }

    [Fact]
    public void A_baked_piece_is_placed_where_the_entity_stands()
    {
        var turned = new Transform(new Vector3(5f, 0f, 3f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), Vector3.One);
        _world.Spawn(turned, new BakedPiece("house", new Vector3(-2f, 0f, -2f), new Vector3(2f, 3f, 2f), TexelsPerMetre: 10));

        Describe();

        var piece = Assert.Single(_view.Indirect.PlacedBakedChunks.ToArray());
        Assert.Equal("house", piece.Name);
        Assert.Equal(turned.Matrix, piece.Transform);
        Assert.Equal(new Vector3(-2f, 0f, -2f), piece.Min);
        Assert.Equal(10, piece.TexelsPerMetre);
    }

    [Fact]
    public void A_hidden_decal_is_not_drawn()
    {
        var shed = _world.Spawn(Transform.Identity, new Hidden());
        _world.Spawn(Transform.Identity, new Decal(new Surface()), new Hidden());
        _world.Spawn(Transform.Identity, new Parent(shed), new Decal(new Surface()));

        Describe();

        Assert.Empty(_view.Effects.DrawnDecals.ToArray());
    }

    [Fact]
    public void A_reflection_capture_outside_its_box_throws_with_the_view_message()
    {
        _world.Spawn(Transform.At(new Vector3(3f, 1f, 2f)), new ReflectionCapture(new Vector3(1f, -1f, -1f), new Vector3(2f)));

        var error = Assert.Throws<ArgumentException>(Describe);

        Assert.StartsWith("The reflection probe's position <3, 1, 2> must be inside its box, from <4, 0, 1> to <5, 3, 4>.", error.Message);
    }
}
