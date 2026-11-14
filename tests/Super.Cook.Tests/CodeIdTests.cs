using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Super.Cook.Tests;

public class CodeIdTests
{
    private const string Engine = "public static class Engine { public const int Size = 4; public static int Twice(int x) => x * 2; }";

    private const string Cooker = "public static class Cooker { public static int Cook(int x) => Engine.Twice(x) + Engine.Size; }";

    [Fact]
    public void Stays_the_same_when_only_a_referenced_assembly_gains_a_method()
    {
        byte[] before = Compile("Cooker", Cooker, Compile("Engine", Engine));
        byte[] after = Compile("Cooker", Cooker, Compile("Engine", Engine.Replace("public const", "public static int Unused() => 1; public const")));

        // The compiler's own id does change, which is why the cook doesn't key on it.
        Assert.NotEqual(Mvid(before), Mvid(after));
        Assert.Equal(CodeId.Of(before), CodeId.Of(after));
    }

    [Fact]
    public void Changes_when_a_method_body_changes()
    {
        byte[] engine = Compile("Engine", Engine);
        byte[] before = Compile("Cooker", Cooker, engine);
        byte[] after = Compile("Cooker", Cooker.Replace("+ Engine.Size", "- Engine.Size"), engine);

        Assert.NotEqual(CodeId.Of(before), CodeId.Of(after));
    }

    [Fact]
    public void Changes_when_a_constant_compiled_in_from_a_referenced_assembly_changes()
    {
        byte[] before = Compile("Cooker", Cooker, Compile("Engine", Engine));
        byte[] after = Compile("Cooker", Cooker, Compile("Engine", Engine.Replace("Size = 4", "Size = 8")));

        Assert.NotEqual(CodeId.Of(before), CodeId.Of(after));
    }

    [Fact]
    public void A_loaded_assembly_gets_the_id_of_its_file()
    {
        var assembly = typeof(CodeId).Assembly;

        Assert.Equal(CodeId.Of(File.ReadAllBytes(assembly.Location)), CodeId.Of(assembly));
    }

    /// <summary>Compiles the way the SDK does: deterministic, with a portable PDB beside the assembly.</summary>
    private static byte[] Compile(string name, string source, params byte[][] references)
    {
        // References are files, as in a real build: the PDB only lists the build ids of references that have a path.
        string folder = Directory.CreateTempSubdirectory("super-codeid-").FullName;
        try
        {
            return Compile(name, source, [.. references.Select((reference, index) =>
            {
                string path = Path.Combine(folder, $"reference{index}.dll");
                File.WriteAllBytes(path, reference);
                return path;
            })]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static byte[] Compile(string name, string source, string[] references)
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source, path: name + ".cs", encoding: System.Text.Encoding.UTF8)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), .. references.Select(reference => MetadataReference.CreateFromFile(reference))],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true, optimizationLevel: OptimizationLevel.Release));

        using var assembly = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(assembly, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: name + ".pdb"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return assembly.ToArray();
    }

    private static Guid Mvid(byte[] image)
    {
        using var reader = new PEReader(new MemoryStream(image));
        MetadataReader metadata = reader.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }
}
