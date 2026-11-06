using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Super.Materials;

namespace Super.Cook.Materials;

public sealed record MaterialSettings
{
    /// <summary>The width and height in pixels: a power of two from 16 to 4096.</summary>
    public int Size { get; init; } = Constants.DefaultSize;
}

/// <summary>Compiles a <c>*.material.cs</c> graph, runs it, and writes its colour, normal, surface and height textures.</summary>
public sealed class MaterialCooker : ICooker
{
    private const int FormatVersion = 1;

    public string Id => "material";

    /// <summary>Changes whenever <c>Super.Materials</c>, <c>Super.Random</c> or this library is rebuilt with a change, so every graph cooks again.</summary>
    public int Version { get; } = FoldVersion();

    public IReadOnlyList<string> Extensions { get; } = [".material.cs", ".material.json"];

    public void Cook(CookInput input, CookOutput output)
    {
        var settings = input.SettingsAs<MaterialSettings>();
        if (settings.Size < Constants.MinSize || settings.Size > Constants.MaxSize || !BitOperations.IsPow2(settings.Size))
        {
            output.Error($"{input.Name}: size must be a power of two from {Constants.MinSize} to {Constants.MaxSize}, not {settings.Size}.");
            return;
        }

        Variant? variant = null;
        string graphName = input.Name;
        byte[] graphSource = input.Bytes;
        if (input.Name.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase))
        {
            variant = Variant.Read(input.Name, input.Bytes, output);
            if (variant is null)
            {
                return;
            }

            // The graph's path is relative to the variant. Reading it makes a change to the graph cook the variant again.
            try
            {
                graphSource = input.Read(variant.Graph);
            }
            catch (FileNotFoundException exception)
            {
                output.Error(exception.Message);
                return;
            }

            graphName = input.Name[..(input.Name.LastIndexOf('/') + 1)] + variant.Graph;
        }

        var compiled = GraphCompiler.Compile(graphName, graphSource, output);
        if (compiled is null)
        {
            return;
        }

        MaterialImages images;
        try
        {
            var graph = (MaterialGraph)Activator.CreateInstance(compiled.Type)!;
            if (variant is not null && !variant.Apply(graph, input.Name, output))
            {
                return;
            }

            images = GraphRunner.Run(graph, settings.Size, new CookImages(input));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var thrown = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            // A cook error is one line. Some exceptions add more, such as the actual value of an argument.
            string message = thrown.Message.Split('\n')[0].TrimEnd('\r');
            output.Error(message, LineIn(thrown, compiled.Type.Assembly));
            return;
        }

        MaterialWriter.Write(images, input.Name, output);
        foreach (string problem in MaterialChecks.Problems(images, input.Name))
        {
            output.Warning(problem);
        }
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
            .. typeof(MaterialCooker).Assembly.ManifestModule.ModuleVersionId.ToByteArray(),
            .. BitConverter.GetBytes(FormatVersion),
        ];
        return BinaryPrimitives.ReadInt32LittleEndian(SHA256.HashData(ids));
    }
}
