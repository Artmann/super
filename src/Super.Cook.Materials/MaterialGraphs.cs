using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Super.Materials;

namespace Super.Cook.Materials;

/// <summary>
/// Compiles and runs a <c>*.material.cs</c> graph, or a <c>*.material.json</c> variant of one. The material cooker runs
/// the file it cooks, and the model cooker runs the graphs a model's materials name, so both report the same errors.
/// </summary>
public static class MaterialGraphs
{
    private const int FormatVersion = 1;

    /// <summary>The size a graph cooks at when nothing sets one, in pixels across.</summary>
    public const int DefaultSize = Constants.DefaultSize;

    /// <summary>Changes whenever <c>Super.Materials</c>, <c>Super.Random</c> or this library is rebuilt with a change, so every graph cooks again.</summary>
    public static int Version { get; } = FoldVersion();

    /// <summary>What is wrong with <paramref name="size"/>, named <paramref name="key"/> in the message, or null when it is a size a graph can cook at.</summary>
    public static string? SizeProblem(string key, int size)
    {
        if (size < Constants.MinSize || size > Constants.MaxSize || !BitOperations.IsPow2(size))
        {
            return $"{key} must be a power of two from {Constants.MinSize} to {Constants.MaxSize}, not {size.ToString(CultureInfo.InvariantCulture)}.";
        }

        return null;
    }

    /// <summary>
    /// Runs the graph or variant at <paramref name="relativePath"/>, relative to the source's folder, at
    /// <paramref name="size"/> pixels across. Reading it through <see cref="CookInput.Read"/> tracks it, its graph and the
    /// images it reads, so a change to any of them cooks the source again. The images are relative to the file named.
    /// </summary>
    /// <param name="error">
    /// Takes each error, such as <c>bricks.material.cs line 7: scale must be at least 1, not 0.</c>. A line is in the
    /// graph's text, since the error is filed under the source, which is another file.
    /// </param>
    /// <returns>The images, or null after reporting why there are none.</returns>
    public static MaterialImages? Run(CookInput input, string relativePath, int size, Action<string> error)
    {
        return Run(input, relativePath.Replace('\\', '/'), size, error, null);
    }

    private static MaterialImages? Run(CookInput input, string relativePath, int size, Action<string> error, List<(string Path, byte[] Hash)>? reads)
    {
        if (!relativePath.EndsWith(".material.cs", StringComparison.OrdinalIgnoreCase)
            && !relativePath.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase))
        {
            error($"{relativePath} isn't a material graph. A graph is a .material.cs or .material.json file.");
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = input.Read(relativePath);
        }
        catch (FileNotFoundException exception)
        {
            error(exception.Message);
            return null;
        }

        return Run(input, relativePath, bytes, size, (message, _) => error(message), reads);
    }

    /// <summary>
    /// Runs the graph or variant at <paramref name="relativePath"/> as <see cref="Run(CookInput, string, int, Action{string})"/>
    /// does, and packs its images into RGBA8 pixels. With a cook cache, the pixels are kept in its step cache under the
    /// graph's code, the variant's values, the size and this library's version, with the hash of every image the graph
    /// read. A later cook with the same graph takes them from there after checking that each image is unchanged, so editing
    /// one of a model's graphs reruns only that graph. A graph can't read anything else, as the graph compiler checks.
    /// </summary>
    /// <remarks>
    /// A run is also shared in the process through <see cref="SharedGraphRuns"/>, under the same key, so another source
    /// that runs the same graph at the same size, such as a second model or the graph's own material, takes it from
    /// memory, and two that want it at once wait for one run.
    /// </remarks>
    /// <returns>The packed images, or null after reporting why there are none to <paramref name="error"/>.</returns>
    public static PackedImages? RunPacked(CookInput input, string relativePath, int size, Action<string> error)
    {
        relativePath = relativePath.Replace('\\', '/');
        string folder = relativePath[..(relativePath.LastIndexOf('/') + 1)];
        string? key;
        using (input.Time("recall graph runs"))
        {
            key = RunKey(input, relativePath, size);
            if (input.HasStepCache && key is not null && input.Recall(RunStep, key) is { } kept && ReadKept(input, folder, kept) is { } packed)
            {
                return packed;
            }
        }

        var run = SharedGraphRuns.GetOrMake(key, input, folder, () =>
        {
            var reads = new List<(string Path, byte[] Hash)>();
            SharedGraphRuns.Run? made;
            using (input.Time("run graphs"))
            {
                made = Run(input, relativePath, size, error, reads) is { } images ? Share(images, reads) : null;
            }

            // Kept by the source that ran it. The others share the run, and the cache it was kept in, with it.
            if (made is not null && key is not null && input.HasStepCache)
            {
                using (input.Time("keep graph runs"))
                {
                    input.Keep(RunStep, key, WriteKept(made.Packed, reads));
                }
            }

            return made;
        });
        return run?.Packed;
    }

    /// <summary>
    /// Runs the source itself, a graph or a variant, as <see cref="RunSource"/> does, or takes the run another source made of
    /// the same file at the same size. An error in the source's own graph has its line.
    /// </summary>
    internal static SharedGraphRuns.Run? RunSourceShared(CookInput input, int size, CookOutput output)
    {
        string? key = RunKey(input, null, size);
        return SharedGraphRuns.GetOrMake(key, input, "", () =>
        {
            var reads = new List<(string Path, byte[] Hash)>();
            using (input.Time("run graphs"))
            {
                return Run(input, null, input.Bytes, size, (message, line) => output.Error(message, line), reads) is { } images
                    ? Share(images, reads)
                    : null;
            }
        });
    }

    private static SharedGraphRuns.Run Share(MaterialImages images, List<(string Path, byte[] Hash)> reads)
    {
        var packed = new PackedImages(images.Size, images.Width, images.HeightDepth, MaterialPixels.Pack(images));
        return new SharedGraphRuns.Run(packed, [.. reads], MaterialChecks.Measure(images));
    }

    /// <summary>
    /// The key of a graph run: everything but the images the graph reads, or null when the graph can't be read, so the run
    /// reports why. <paramref name="relativePath"/> is null for the source itself.
    /// </summary>
    private static string? RunKey(CookInput input, string? relativePath, int size)
    {
        string fileName = relativePath ?? input.Name;
        if (!fileName.EndsWith(".material.cs", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            byte[] bytes = relativePath is null ? input.Bytes : input.Read(relativePath);
            byte[] graph = bytes;
            if (fileName.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase))
            {
                if (Variant.Read(fileName, bytes, _ => { }) is not { } variant)
                {
                    return null;
                }

                string folder = relativePath is null ? "" : relativePath[..(relativePath.LastIndexOf('/') + 1)];
                graph = input.Read(folder + variant.Graph);
            }

            return string.Join('|',
                Version.ToString(CultureInfo.InvariantCulture),
                KeptFormat.ToString(CultureInfo.InvariantCulture),
                size.ToString(CultureInfo.InvariantCulture),
                relativePath is null ? input.Name : RootName(input.Name, relativePath),
                Convert.ToHexString(SHA256.HashData(bytes)),
                Convert.ToHexString(SHA256.HashData(graph)));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private const string RunStep = "graph-run";

    // 1: the images read, then the packed pixels, with the height's red only.
    // 2: the same, with the pixels as planes of differences, Brotli compressed.
    private const int KeptFormat = 2;

    /// <summary>
    /// How hard a kept run's pixels are compressed. Over Courtyard's runs, quality 1 on planes of differences keeps 22% of
    /// the bytes and takes about 0.15 s each way for a 2048 run, against seconds to run the graph again; higher qualities
    /// save a few percent more for several times the time.
    /// </summary>
    private const int KeptQuality = 1;

    private static byte[] WriteKept(PackedImages run, List<(string Path, byte[] Hash)> reads)
    {
        var pixels = run.Pixels;
        var stream = new MemoryStream(pixels.Color.Length + 1024);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(reads.Count);
            foreach (var (path, hash) in reads.OrderBy(read => read.Path, StringComparer.Ordinal))
            {
                writer.Write(path);
                writer.Write(hash);
            }

            writer.Write(run.Size);
            writer.Write(run.Width);
            writer.Write(run.HeightDepth);
            writer.Write(pixels.NotNumbers.Count);
            foreach (string part in pixels.NotNumbers)
            {
                writer.Write(part);
            }

        }

        // Each channel on its own, as the difference from the pixel before, so the smooth images a graph makes turn into
        // runs of small numbers that compress far better than the pixels do.
        using (var brotli = new BrotliStream(stream, new BrotliCompressionOptions { Quality = KeptQuality }, leaveOpen: true))
        {
            var plane = new byte[pixels.Color.Length / 4];
            foreach (byte[] image in (byte[][])[pixels.Color, pixels.Normal, pixels.Surface])
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    WritePlane(brotli, plane, image, channel);
                }
            }

            WritePlane(brotli, plane, pixels.Height, 0);
        }

        return stream.ToArray();
    }

    /// <summary>Writes one channel of an RGBA image, each byte as its difference from the one before it.</summary>
    private static void WritePlane(Stream stream, byte[] plane, byte[] image, int channel)
    {
        byte previous = 0;
        for (int i = 0, at = channel; i < plane.Length; i++, at += 4)
        {
            byte value = image[at];
            plane[i] = (byte)(value - previous);
            previous = value;
        }

        stream.Write(plane);
    }

    /// <summary>Reads one channel written by <see cref="WritePlane"/> into an RGBA image.</summary>
    private static void ReadPlane(Stream stream, byte[] plane, byte[] image, int channel)
    {
        stream.ReadExactly(plane);
        byte value = 0;
        for (int i = 0, at = channel; i < plane.Length; i++, at += 4)
        {
            value += plane[i];
            image[at] = value;
        }
    }

    /// <returns>The kept run, or null when an image it read has changed or is gone, or the entry doesn't read.</returns>
    private static PackedImages? ReadKept(CookInput input, string folder, byte[] kept)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(kept));
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string path = reader.ReadString();
                byte[] hash = reader.ReadBytes(32);

                // Reading the image tracks it, as running the graph would.
                if (!SHA256.HashData(input.Read(folder + path)).AsSpan().SequenceEqual(hash))
                {
                    return null;
                }
            }

            int size = reader.ReadInt32();
            float width = reader.ReadSingle();
            float heightDepth = reader.ReadSingle();
            var notNumbers = new string[reader.ReadInt32()];
            for (int i = 0; i < notNumbers.Length; i++)
            {
                notNumbers[i] = reader.ReadString();
            }

            if (SizeProblem("size", size) is not null)
            {
                return null;
            }

            int length = size * size * 4;
            var color = new byte[length];
            var normal = new byte[length];
            var surface = new byte[length];
            var height = new byte[length];
            try
            {
                using var brotli = new BrotliStream(reader.BaseStream, CompressionMode.Decompress, leaveOpen: true);
                var plane = new byte[length / 4];
                foreach (byte[] image in (byte[][])[color, normal, surface])
                {
                    for (int channel = 0; channel < 4; channel++)
                    {
                        ReadPlane(brotli, plane, image, channel);
                    }
                }

                ReadPlane(brotli, plane, height, 0);
                if (brotli.ReadByte() >= 0)
                {
                    return null;
                }
            }
            catch (InvalidOperationException)
            {
                // BrotliStream says "Decoder ran into invalid data" this way, not with InvalidDataException.
                return null;
            }

            for (int i = 0; i < length; i += 4)
            {
                height[i + 1] = height[i];
                height[i + 2] = height[i];
                height[i + 3] = 255;
            }

            return new PackedImages(size, width, heightDepth, new MaterialPixels(color, normal, surface, height, notNumbers));
        }
        catch (Exception exception) when (exception is EndOfStreamException or FileNotFoundException or OverflowException
            or FormatException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>Runs the source itself, a graph or a variant. An error in the source's own graph has its line.</summary>
    internal static MaterialImages? RunSource(CookInput input, int size, CookOutput output)
    {
        return Run(input, null, input.Bytes, size, (message, line) => output.Error(message, line));
    }

    /// <param name="path">The file's path from the source's folder, or null for the source itself.</param>
    private static MaterialImages? Run(
        CookInput input, string? path, byte[] bytes, int size, Action<string, int?> report, List<(string Path, byte[] Hash)>? reads = null)
    {
        string fileName = path ?? input.Name;
        string folder = path is null ? "" : path[..(path.LastIndexOf('/') + 1)];

        // The graph's path from the source's folder, or null when the source is the graph. Its errors name it, since a
        // line in the graph means nothing in the file the error is filed under.
        string? graphPath = path;
        Variant? variant = null;
        if (fileName.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase))
        {
            variant = Variant.Read(fileName, bytes, message => report(message, null));
            if (variant is null)
            {
                return null;
            }

            // The graph's path is relative to the variant. Reading it makes a change to the graph cook the source again.
            graphPath = folder + variant.Graph;
            try
            {
                bytes = input.Read(graphPath);
            }
            catch (FileNotFoundException exception)
            {
                report(exception.Message, null);
                return null;
            }
        }

        string graphName = graphPath is null ? input.Name : RootName(input.Name, graphPath);

        void GraphError(string message, int? line)
        {
            if (graphPath is null)
            {
                report(message, line);
                return;
            }

            string own = graphName + ": ";
            if (message.StartsWith(own, StringComparison.Ordinal))
            {
                message = message[own.Length..];
            }

            string where = line is { } number ? $"{graphPath} line {number.ToString(CultureInfo.InvariantCulture)}" : graphPath;
            report($"{where}: {message}", null);
        }

        var compiled = GraphCompiler.Compile(graphName, bytes, GraphError);
        if (compiled is null)
        {
            return null;
        }

        try
        {
            var graph = (MaterialGraph)Activator.CreateInstance(compiled.Type)!;
            if (variant is not null && !variant.Apply(graph, fileName, message => report(message, null)))
            {
                return null;
            }

            SharedGraphRuns.CountRun();
            return GraphRunner.Run(graph, size, new CookImages(input, folder, reads));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var thrown = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            // A cook error is one line. Some exceptions add more, such as the actual value of an argument.
            string message = thrown.Message.Split('\n')[0].TrimEnd('\r');
            GraphError(message, LineIn(thrown, compiled.Type.Assembly));
            return null;
        }
    }

    /// <summary>The name in the source root of <paramref name="relative"/>, a path from <paramref name="sourceName"/>'s folder.</summary>
    private static string RootName(string sourceName, string relative)
    {
        var parts = new List<string>(sourceName.Split('/')[..^1]);
        foreach (string part in relative.Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == ".." && parts.Count > 0)
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>The line of the first stack frame in the graph's own code, or null when the graph's code isn't on the stack.</summary>
    private static int? LineIn(Exception exception, Assembly graph)
    {
        foreach (var frame in new StackTrace(exception, fNeedFileInfo: true).GetFrames())
        {
            if (frame.GetMethod()?.Module.Assembly == graph && frame.GetFileLineNumber() is > 0 and var line)
            {
                return line;
            }
        }

        return null;
    }

    private static int FoldVersion()
    {
        byte[] ids = [
            .. typeof(MaterialGraph).Assembly.ManifestModule.ModuleVersionId.ToByteArray(),
            .. typeof(GameRandom).Assembly.ManifestModule.ModuleVersionId.ToByteArray(),
            .. typeof(MaterialGraphs).Assembly.ManifestModule.ModuleVersionId.ToByteArray(),
            .. BitConverter.GetBytes(FormatVersion),
        ];
        return BinaryPrimitives.ReadInt32LittleEndian(SHA256.HashData(ids));
    }
}
