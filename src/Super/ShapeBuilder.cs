using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// One point on the outline a round shape is spun from: how far out it is, how high,
/// which way it faces in that half-slice, and how far along the outline it lies.
/// </summary>
internal readonly record struct ProfilePoint(float Radius, float Y, Vector2 Normal, float Distance);

/// <summary>
/// One piece of a shape in the lightmap: its vertices from <paramref name="Start"/> on,
/// where each lies on the flat chart in metres, and how wide and tall the chart is.
/// </summary>
internal sealed record MeshChart(int Start, Vector2[] Layout, Vector2 Extent);

/// <summary>
/// Collects the vertices, triangles and lightmap charts of a shape from <see cref="Shapes"/>, piece by piece,
/// and packs the charts on one lightmap page when it's done.
/// </summary>
internal sealed class ShapeBuilder
{
    private readonly List<Vertex> _vertices = [];
    private readonly List<uint> _indices = [];
    private readonly List<MeshChart> _charts = [];

    /// <summary>
    /// The mesh, with every piece's lightmap chart packed on one page and its vertices'
    /// <see cref="Vertex.Uv2"/> set from it.
    /// </summary>
    public GeneratedMesh ToMesh(Surface? surface)
    {
        LayOutLightmap();
        return new([.. _vertices], [.. _indices], surface ?? new Surface());
    }

    /// <summary>
    /// A grid of vertices, row by row from the top. Rows run down the texture and
    /// columns across it. A triangle whose corners meet, such as at a pole, is left out.
    /// The grid is one lightmap chart, laid out by its columns and rows.
    /// </summary>
    public void AddGrid(int columns, int rows, Func<int, int, Vertex> vertexAt)
    {
        uint start = (uint)_vertices.Count;
        for (int row = 0; row <= rows; row++)
        {
            for (int column = 0; column <= columns; column++)
            {
                _vertices.Add(vertexAt(column, row));
            }
        }

        AddGridChart((int)start, columns, rows);

        uint stride = (uint)columns + 1;
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                uint corner = start + (uint)row * stride + (uint)column;
                uint below = corner + stride;
                Triangle(corner, below, corner + 1);
                Triangle(corner + 1, below, below + 1);
            }
        }
    }

    /// <summary>
    /// Spins an outline round the y axis. Its points go down the outside of the shape,
    /// and the texture wraps once round, joined at a seam facing along x.
    /// </summary>
    public void Revolve(IReadOnlyList<ProfilePoint> profile, int segments, float widest, float? uvPerMetre)
    {
        float length = profile[^1].Distance;
        var uvScale = uvPerMetre is { } perMetre ? ShapeMath.Tiling(perMetre, 2f * MathF.PI * widest, 1f) : new Vector2(1f, 1f / length);
        AddGrid(segments, profile.Count - 1, (column, row) =>
        {
            // The seam's last column takes the same angle as the first, so they meet exactly.
            var (sin, cos) = Around(column % segments, segments);
            var point = profile[row];
            var tangent = new Vector4(-sin, 0f, -cos, 1f);
            return new Vertex(
                new Vector3(point.Radius * cos, point.Y, -point.Radius * sin),
                new Vector3(point.Normal.X * cos, point.Normal.Y, -point.Normal.X * sin),
                tangent,
                new Vector2(column / (float)segments, point.Distance) * uvScale);
        });
    }

    /// <summary>A flat round end at this height, facing up or down, with its own sharp-edged normals.</summary>
    public void AddDisc(float y, float radius, int segments, bool facingUp, float? uvPerMetre)
    {
        uint centre = (uint)_vertices.Count;
        AddDiscVertices(y, radius, segments, facingUp, uvPerMetre);
        AddDiscChart((int)centre, radius, segments, facingUp);
        AddDiscTriangles(centre, segments, facingUp);
    }

    /// <summary>The disc's centre, then its rim.</summary>
    private void AddDiscVertices(float y, float radius, int segments, bool facingUp, float? uvPerMetre)
    {
        var normal = facingUp ? Vector3.UnitY : -Vector3.UnitY;
        var tangent = new Vector4(Vector3.UnitX, 1f);

        // Seen from outside, v runs towards +z on top and towards -z underneath.
        float flip = facingUp ? 1f : -1f;
        var uvScale = uvPerMetre is { } perMetre ? new Vector2(perMetre) : new Vector2(0.5f / radius);
        var uvCentre = uvPerMetre is null ? new Vector2(0.5f) : Vector2.Zero;

        _vertices.Add(new Vertex(new Vector3(0f, y, 0f), normal, tangent, uvCentre));
        for (int i = 0; i < segments; i++)
        {
            var (sin, cos) = Around(i, segments);
            var position = new Vector3(radius * cos, y, -radius * sin);
            _vertices.Add(new Vertex(position, normal, tangent, uvCentre + new Vector2(position.X, position.Z * flip) * uvScale));
        }
    }

    /// <summary>The disc's chart is the disc itself, seen from the side it faces.</summary>
    private void AddDiscChart(int centre, float radius, int segments, bool facingUp)
    {
        float flip = facingUp ? 1f : -1f;
        var layout = new Vector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            var position = _vertices[centre + i].Position;
            layout[i] = new Vector2(position.X + radius, position.Z * flip + radius);
        }

        _charts.Add(new MeshChart(centre, layout, new Vector2(2f * radius)));
    }

    /// <summary>A fan from the centre to each step of the rim.</summary>
    private void AddDiscTriangles(uint centre, int segments, bool facingUp)
    {
        for (int i = 0; i < segments; i++)
        {
            uint here = centre + 1 + (uint)i;
            uint next = centre + 1 + (uint)((i + 1) % segments);

            // Round the rim goes anticlockwise from above, so the top keeps that order.
            if (facingUp)
            {
                Triangle(centre, here, next);
            }
            else
            {
                Triangle(centre, next, here);
            }
        }
    }

    /// <summary>
    /// Lays a grid out flat in metres by its columns and rows: each column is as wide as
    /// its widest row makes it, and each row as tall as its tallest column, so the chart
    /// is never squeezed smaller than the surface, only stretched where it narrows, such
    /// as towards a sphere's poles.
    /// </summary>
    private void AddGridChart(int start, int columns, int rows)
    {
        int stride = columns + 1;
        var xs = ColumnEdges(start, columns, rows);
        var ys = RowEdges(start, columns, rows);
        var layout = new Vector2[(rows + 1) * stride];
        for (int row = 0; row <= rows; row++)
        {
            // A row that's all one point, such as a pole or a cone's tip, is one point in
            // the chart too, so the triangles round it join along their edges.
            bool point = RowIsPoint(start + row * stride, columns);
            for (int column = 0; column <= columns; column++)
            {
                float x = point ? xs[columns] * 0.5f : xs[column];
                layout[row * stride + column] = new Vector2(x, ys[row]);
            }
        }

        _charts.Add(new MeshChart(start, layout, new Vector2(xs[columns], ys[rows])));
    }

    /// <summary>Where each column of a grid starts across its chart: the sum of the widest rows before it.</summary>
    private float[] ColumnEdges(int start, int columns, int rows)
    {
        int stride = columns + 1;
        var xs = new float[columns + 1];
        for (int column = 0; column < columns; column++)
        {
            float widest = 0f;
            for (int row = 0; row <= rows; row++)
            {
                int at = start + row * stride + column;
                widest = MathF.Max(widest, Vector3.Distance(_vertices[at].Position, _vertices[at + 1].Position));
            }

            xs[column + 1] = xs[column] + widest;
        }

        return xs;
    }

    /// <summary>Where each row of a grid starts down its chart: the sum of the tallest columns before it.</summary>
    private float[] RowEdges(int start, int columns, int rows)
    {
        int stride = columns + 1;
        var ys = new float[rows + 1];
        for (int row = 0; row < rows; row++)
        {
            float tallest = 0f;
            for (int column = 0; column <= columns; column++)
            {
                int at = start + row * stride + column;
                tallest = MathF.Max(tallest, Vector3.Distance(_vertices[at].Position, _vertices[at + stride].Position));
            }

            ys[row + 1] = ys[row] + tallest;
        }

        return ys;
    }

    /// <summary>Whether every vertex of a grid row is in the same place.</summary>
    private bool RowIsPoint(int first, int columns)
    {
        var position = _vertices[first].Position;
        for (int column = 1; column <= columns; column++)
        {
            if (_vertices[first + column].Position != position)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Packs every chart on one lightmap page at the same texels per metre, as many as
    /// fit, with <see cref="LightmapUnwrap.Gutter"/> texels between them, and sets the
    /// vertices' <see cref="Vertex.Uv2"/>. The page is
    /// <see cref="LightmapUnwrap.ReferenceSize"/> texels square, or twice or four times
    /// that when the charts don't fit even at their smallest.
    /// </summary>
    private void LayOutLightmap()
    {
        if (_charts.Count == 0)
        {
            return;
        }

        var packing = new ChartPacking([.. _charts.Select(chart => chart.Extent)]);
        for (int size = LightmapUnwrap.ReferenceSize; size <= LightmapUnwrap.LargestSize; size *= 2)
        {
            if (packing.BestScale(size) is { } fits)
            {
                SetLightmapUvs(packing, fits, size);
                return;
            }
        }

        throw new InvalidOperationException(
            $"The shape's {_charts.Count} lightmap charts don't fit a {LightmapUnwrap.LargestSize}-texel page with a {LightmapUnwrap.Gutter}-texel gutter, even at their smallest.");
    }

    /// <summary>Sets each chart's vertices' <see cref="Vertex.Uv2"/>, centred in its packed rectangle.</summary>
    private void SetLightmapUvs(ChartPacking packing, double fits, int size)
    {
        for (int i = 0; i < _charts.Count; i++)
        {
            var chart = _charts[i];
            var (left, top) = packing.Corner(i, fits);
            for (int k = 0; k < chart.Layout.Length; k++)
            {
                double u = (left + chart.Layout[k].X * fits) / size;
                double v = (top + chart.Layout[k].Y * fits) / size;
                _vertices[chart.Start + k] = _vertices[chart.Start + k] with { Uv2 = new Vector2((float)u, (float)v) };
            }
        }
    }

    private static (float Sin, float Cos) Around(int step, int steps) => ShapeMath.SinCos(2f * MathF.PI * step / steps);

    private void Triangle(uint a, uint b, uint c)
    {
        var pa = _vertices[(int)a].Position;
        var pb = _vertices[(int)b].Position;
        var pc = _vertices[(int)c].Position;
        if (pa == pb || pb == pc || pa == pc)
        {
            return;
        }

        _indices.Add(a);
        _indices.Add(b);
        _indices.Add(c);
    }
}
