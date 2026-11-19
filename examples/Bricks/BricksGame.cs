using System.Numerics;
using Super;

namespace BricksExample;

/// <summary>
/// A brick wall beside a street of setts, seen from a camera walking slowly along it, so
/// the wall is seen at a grazing angle. The bricks and setts use height maps and occlusion
/// maps, which darken the sky's light in the joints, and the street a detail texture of grit.
/// P turns the height maps off, D the detail and O the occlusion.
/// </summary>
public sealed class BricksGame(World world, Content content, Input input) : IGame
{
    private const float WallLength = 16f;
    private const float WallHeight = 3f;
    private const float WalkSpeed = 0.4f;

    // A brick texture holds 8 rows of 4 bricks: half a metre across and 0.4 m high.
    private const float BrickTileMetres = 0.9f;
    private const float SettTileMetres = 1.2f;

    private (Look Occluded, Look Open) _wall;
    private (Look Occluded, Look Open) _street;
    private bool _flat;
    private bool _noDetail;
    private bool _noOcclusion;
    private Entity _wallEntity;
    private Entity _streetEntity;
    private Entity _camera;
    private float _walked;

    /// <summary>The same shape with and without its height map and detail, so either can be shown.</summary>
    private readonly record struct Look(ModelRef Full, ModelRef Flat, ModelRef NoDetail, ModelRef Plain);

    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        ["flat"] = new InputScript().Press(Key.P),
        ["no-detail"] = new InputScript().Press(Key.D),
        ["plain"] = new InputScript().Press(Key.P).Press(Key.D),
        ["no-occlusion"] = new InputScript().Press(Key.O),
    };

    public void Load()
    {
        const float brickDepth = 0.025f;
        var bricks = Masonry.Bricks(512, columns: 4, rows: 8, BrickTileMetres, brickDepth);
        var brickSurface = Textured(bricks, brickDepth, 0.85f);
        _wall = Shaped(Shapes.Box(new Vector3(WallLength, WallHeight, 0.4f), uvPerMetre: 1f / BrickTileMetres), brickSurface, null);

        const float settDepth = 0.03f;
        var setts = Masonry.Setts(512, across: 6, SettTileMetres, settDepth);
        var (gritColor, gritNormal) = Masonry.Grit(256);
        var grit = (content.Texture(gritColor, Rendition.TextureKind.Linear), content.Texture(gritNormal, Rendition.TextureKind.Linear, normalMap: true));
        _street = Shaped(Shapes.Plane(new Vector2(WallLength + 8f, 12f), uvPerMetre: 1f / SettTileMetres), Textured(setts, settDepth, 0.75f), grit);
        world.Spawn(new Sun(new Vector3(0.9f, 0.7f, 0.6f), new Vector3(3.2f, 3f, 2.7f)), new Shadows(12f, 2));
        _wallEntity = world.Spawn(Transform.At(new Vector3(0f, WallHeight * 0.5f, -0.2f)), new Model(Pick(_wall)));
        _streetEntity = world.Spawn(Transform.At(new Vector3(0f, 0f, 5.6f)), new Model(Pick(_street)));
        _camera = world.Spawn(Camera());
    }

    public void Update(GameTime time)
    {
        if (input.Pressed(Key.P))
        {
            _flat = !_flat;
        }

        if (input.Pressed(Key.D))
        {
            _noDetail = !_noDetail;
        }

        if (input.Pressed(Key.O))
        {
            _noOcclusion = !_noOcclusion;
        }

        world.Get<Model>(_wallEntity) = new Model(Pick(_wall));
        world.Get<Model>(_streetEntity) = new Model(Pick(_street));
        _walked += WalkSpeed * time.TickSeconds;
        world.Get<Camera>(_camera) = Camera();
    }

    // The camera walks slowly along the wall, looking ahead at it.
    private Camera Camera()
    {
        var eye = new Vector3(-5f + _walked, 1.6f, 2.2f);
        return new Camera(eye, eye + new Vector3(4f, -0.5f, -1.6f), 0.9f);
    }

    private ModelRef Pick((Look Occluded, Look Open) looks)
    {
        var look = _noOcclusion ? looks.Open : looks.Occluded;
        return (_flat, _noDetail) switch
        {
            (false, false) => look.Full,
            (true, false) => look.Flat,
            (false, true) => look.NoDetail,
            _ => look.Plain,
        };
    }

    private Surface Textured(Masonry masonry, float depth, float roughness) => new()
    {
        Color = Vector3.One,
        Roughness = roughness,
        ColorTexture = content.Texture(masonry.Color),
        NormalTexture = content.Texture(masonry.Normal, Rendition.TextureKind.Linear, normalMap: true),
        Height = new HeightMap(content.Texture(masonry.Height, Rendition.TextureKind.Linear), depth),
        Occlusion = new OcclusionMap(content.Texture(masonry.Occlusion, Rendition.TextureKind.Linear)),
    };

    /// <summary>The shape with its occlusion map and without, each in every mix of height map and detail.</summary>
    private (Look Occluded, Look Open) Shaped(GeneratedMesh mesh, Surface surface, (TextureRef Color, TextureRef Normal)? detail)
    {
        Look Looks(Surface surface)
        {
            var detailed = detail is { } d ? surface with { Detail = new DetailMap { ColorTexture = d.Color, NormalTexture = d.Normal, Scale = 5f } } : surface;
            var flat = detailed with { Height = null };
            return new Look(
                content.Mesh(mesh with { Surface = detailed }),
                content.Mesh(mesh with { Surface = flat }),
                content.Mesh(mesh with { Surface = surface }),
                content.Mesh(mesh with { Surface = surface with { Height = null } }));
        }

        return (Looks(surface), Looks(surface with { Occlusion = null }));
    }
}
