namespace Super.Materials;

/// <summary>A decoded picture, resampled to the size the graph runs at. Alpha is ignored and colours are as decoded, 0..1.</summary>
sealed class ImageInputNode : Node
{
    readonly DecodedImage _image;

    public ImageInputNode(DecodedImage image) : base(3)
    {
        if (image.Width < 1 || image.Height < 1 || image.Rgba.Length != image.Width * image.Height * 4)
        {
            throw new ArgumentException($"A {image.Width} by {image.Height} image needs {image.Width * image.Height * 4} values, not {image.Rgba.Length}.", nameof(image));
        }

        _image = image;
    }

    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var width = _image.Width;
        var height = _image.Height;
        var rgba = _image.Rgba;

        context.Rows(y =>
        {
            var v = (y + 0.5f) / context.Size;
            var fy = v * height - 0.5f;
            var y0 = (int)MathF.Floor(fy);
            var ty = fy - y0;
            var y1 = Sampling.Wrap(y0 + 1, height);

            y0 = Sampling.Wrap(y0, height);

            for (var x = 0; x < context.Size; x++)
            {
                var u = (x + 0.5f) / context.Size;
                var fx = u * width - 0.5f;
                var x0 = (int)MathF.Floor(fx);
                var tx = fx - x0;
                var x1 = Sampling.Wrap(x0 + 1, width);

                x0 = Sampling.Wrap(x0, width);

                for (var c = 0; c < 3; c++)
                {
                    float At(int px, int py) => rgba[(py * width + px) * 4 + c];

                    var top = At(x0, y0) + (At(x1, y0) - At(x0, y0)) * tx;
                    var bottom = At(x0, y1) + (At(x1, y1) - At(x0, y1)) * tx;

                    output[(y * context.Size + x) * 3 + c] = top + (bottom - top) * ty;
                }
            }
        });
    }
}
