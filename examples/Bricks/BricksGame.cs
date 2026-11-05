using System.Numerics;
using Super;

namespace BricksExample;

/// <summary>
/// A brick wall beside a street of setts, seen from a camera walking slowly along it, so
/// the wall is seen at a grazing angle. The bricks and setts use height maps, and the
/// street a detail texture of grit. P turns the height maps off and D the detail.
/// </summary>
public sealed class BricksGame(Content content, Input input) : IGame
{
    private const float WallLength = 16f;
    private const float WallHeight = 3f;
    private const float WalkSpeed = 0.4f;

    // A brick texture holds 8 rows of 4 bricks: half a metre across and 0.4 m high.
    private const float BrickTileMetres = 0.9f;
    private const float SettTileMetres = 1.2f;

    private Look _wall;
    private Look _street;
    private bool _flat;
    private bool _noDetail;
    private float _walked;
    private float _previousWalked;

    /// <summary>The same shape with and without its height map and detail, so either can be shown.</summary>
    private readonly record struct Look(ModelRef Full, ModelRef Flat, ModelRef NoDetail, ModelRef Plain);

    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        ["flat"] = new InputScript().Press(Key.P),
        ["no-detail"] = new InputScript().Press(Key.D),
        ["plain"] = new InputScript().Press(Key.P).Press(Key.D),
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

        _previousWalked = _walked;
        _walked += WalkSpeed * time.TickSeconds;
    }

    public void Draw(View view)
    {
        float walked = float.Lerp(_previousWalked, _walked, view.Blend);
        var eye = new Vector3(-5f + walked, 1.6f, 2.2f);
        view.Camera(position: eye, lookAt: eye + new Vector3(4f, -0.5f, -1.6f), fieldOfView: 0.9f);
        view.Sun(direction: new Vector3(0.9f, 0.7f, 0.6f), color: new Vector3(3.2f, 3f, 2.7f));
        view.Shadows(12f, 2);

        view.Model(0, Pick(_wall), Matrix4x4.CreateTranslation(0f, WallHeight * 0.5f, -0.2f));
        view.Model(1, Pick(_street), Matrix4x4.CreateTranslation(0f, 0f, 5.6f));
    }

    private ModelRef Pick(Look look) => (_flat, _noDetail) switch
    {
        (false, false) => look.Full,
        (true, false) => look.Flat,
        (false, true) => look.NoDetail,
        _ => look.Plain,
    };

    private Surface Textured(Masonry masonry, float depth, float roughness) => new()
    {
        Color = Vector3.One,
        Roughness = roughness,
        ColorTexture = content.Texture(masonry.Color),
        NormalTexture = content.Texture(masonry.Normal, Rendition.TextureKind.Linear, normalMap: true),
        HeightTexture = content.Texture(masonry.Height, Rendition.TextureKind.Linear),
        HeightDepth = depth,
    };

    private Look Shaped(GeneratedMesh mesh, Surface surface, (TextureRef Color, TextureRef Normal)? detail)
    {
        var detailed = detail is { } d ? surface with { DetailColorTexture = d.Color, DetailNormalTexture = d.Normal, DetailScale = 5f } : surface;
        var flat = detailed with { HeightTexture = null };
        return new Look(
            content.Mesh(mesh with { Surface = detailed }),
            content.Mesh(mesh with { Surface = flat }),
            content.Mesh(mesh with { Surface = surface }),
            content.Mesh(mesh with { Surface = surface with { HeightTexture = null } }));
    }
}
