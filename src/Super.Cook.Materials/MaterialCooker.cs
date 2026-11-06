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

    /// <summary>Changes whenever <c>Super.Materials</c> or this library is rebuilt with a change, so every graph cooks again.</summary>
    public int Version { get; } = FoldVersion();

    public IReadOnlyList<string> Extensions { get; } = [".material.cs", ".material.json"];

    public void Cook(CookInput input, CookOutput output)
    {
        if (input.Name.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase))
        {
            output.Error($"{input.Name}: material variants aren't supported yet.");
            return;
        }

        var settings = input.SettingsAs<MaterialSettings>();
        if (settings.Size < Constants.MinSize || settings.Size > Constants.MaxSize || !BitOperations.IsPow2(settings.Size))
        {
            output.Error($"{input.Name}: size must be a power of two from {Constants.MinSize} to {Constants.MaxSize}, not {settings.Size}.");
            return;
        }

        var compiled = GraphCompiler.Compile(input.Name, input.Bytes, output);
        if (compiled is null)
        {
            return;
        }

        MaterialImages images;
        try
        {
            var graph = (MaterialGraph)Activator.CreateInstance(compiled.Type)!;
            images = GraphRunner.Run(graph, settings.Size);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var thrown = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            output.Error(thrown.Message, LineIn(thrown, compiled.Type.Assembly));
            return;
        }

        MaterialWriter.Write(images, input.Name, output);
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
            .. typeof(MaterialCooker).Assembly.ManifestModule.ModuleVersionId.ToByteArray(),
            .. BitConverter.GetBytes(FormatVersion),
        ];
        return BinaryPrimitives.ReadInt32LittleEndian(SHA256.HashData(ids));
    }
}
