using System.Globalization;
using System.Numerics;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// A frame's grass, checked and planned on the CPU: its draws and their looks, the squares of the lattice the camera
/// sees, and the uniforms the shaders read.
/// </summary>
internal sealed class GrassPlan(MeshStore meshes, ResourceTable resources)
{
    // The ground map's heights reach this far below and above the camera.
    private const float CaptureBelow = 1000f;
    private const float CaptureRange = 2000f;

    private readonly GrassTile[] _tiles = new GrassTile[GrassField.MaxTiles];
    private readonly GrassSettings[] _looks = new GrassSettings[GrassField.MaxKinds];
    private GrassDraw[] _draws = [];
    private int[] _drawKinds = [];
    private int _drawCount;
    private int _tileCount;
    private GrassUniforms _uniforms;

    /// <summary>The frame's grass draws.</summary>
    public ReadOnlySpan<GrassDraw> Draws => _draws.AsSpan(0, _drawCount);

    /// <summary>The look each of <see cref="Draws"/> has, as its index in the uniforms' kinds.</summary>
    public ReadOnlySpan<int> DrawKinds => _drawKinds.AsSpan(0, _drawCount);

    /// <summary>The squares the camera sees, which the scatter runs one group each over.</summary>
    public ReadOnlySpan<GrassTile> Tiles => _tiles.AsSpan(0, _tileCount);

    /// <summary>The uniforms the grass shaders read.</summary>
    public ref readonly GrassUniforms Uniforms => ref _uniforms;

    /// <summary>
    /// Checks the view's grass and plans it at <paramref name="detail"/>. Throws for grass that can't be drawn: a look
    /// out of range, too many looks, a ground mesh or density map that doesn't exist, or a channel
    /// other than 0 to 3.
    /// </summary>
    public void Prepare(in FrameView view, float detail)
    {
        var grass = view.Extras.Grass;
        if (_draws.Length < grass.Length)
        {
            _draws = new GrassDraw[grass.Length];
            _drawKinds = new int[grass.Length];
        }

        int kinds = 0;
        for (int i = 0; i < grass.Length; i++)
        {
            Check(grass[i], i);
            _draws[i] = grass[i];
            _drawKinds[i] = GrassField.KindOf(_looks, ref kinds, grass[i].Look);
        }

        _drawCount = grass.Length;
        var camera = view.CameraPosition;
        var radii = GrassField.Radii(detail);
        GrassField.Planes(view.View * view.Projection, _uniforms.Planes);
        _tileCount = GrassField.Plan(camera, _uniforms.Planes, radii, _tiles);
        var (corner, texel) = GrassField.CaptureArea(camera, radii);
        _uniforms.Capture = new Vector4(corner, texel, GrassField.CaptureSize);
        _uniforms.Heights = new Vector4(camera.Y - CaptureBelow, CaptureRange, 0f, 0f);
        _uniforms.Rings = radii;
        _uniforms.Camera = new Vector4(camera, GrassField.FadeShare);
        _uniforms.Details = GrassField.DetailDistances(radii);
        _uniforms.Tiles = _tileCount;
        _uniforms.FirstRegion = GrassField.DetailStart(0);
        _uniforms.SecondRegion = GrassField.DetailStart(1);
        _uniforms.ThirdRegion = GrassField.DetailStart(2);
        _uniforms.AllBlades = GrassField.DetailStart(GrassField.Details);
        for (int i = 0; i < kinds; i++)
        {
            _uniforms.Kinds[i] = GrassKindData.Of(_looks[i]);
        }
    }

    private void Check(in GrassDraw draw, int index)
    {
        if (GrassSettings.Problem(draw.Look) is { } problem)
        {
            throw new ArgumentException(Invariant($"Grass {index}: {problem}"), "view");
        }

        var mesh = draw.Ground.Mesh;
        if (mesh.Index < 0 || mesh.Index >= meshes.MeshSlots.Count || meshes.MeshSlots[mesh.Index] is null)
        {
            throw new ArgumentException(Invariant($"Grass {index}'s ground mesh does not exist."), "view");
        }

        if (draw.Density is not { } density)
        {
            return;
        }

        if (GrassDensity.Problem(density) is { } densityProblem)
        {
            throw new ArgumentException(Invariant($"Grass {index}: {densityProblem}"), "view");
        }

        if (!resources.Textures.IsLive(density.Map.Index))
        {
            throw new ArgumentException(Invariant($"Grass {index}'s density map does not exist."), "view");
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
