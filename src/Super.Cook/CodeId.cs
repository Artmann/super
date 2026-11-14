using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Super.Cook;

/// <summary>
/// Names an assembly's code, for cache keys that must miss when the code changes and only then.
///
/// The compiler's own id for a build, the module version id, changes more often than the code does. It is a hash of
/// the whole file, and the file points at its PDB by a checksum. The PDB lists the build id of every assembly the
/// code was compiled against, so a new internal method anywhere in the engine gave each cook library a new id and
/// every bake ran again. This id hashes the file without the parts that only name the build: the module version id,
/// the timestamp made from it and the debug directory. What is left is the code, its constants and its metadata.
/// </summary>
internal static class CodeId
{
    private static readonly ConcurrentDictionary<Assembly, Guid> Known = new();

    /// <summary>
    /// The id of a loaded assembly's code. An assembly with no file to read, such as one made in memory, falls back to
    /// its module version id, which is never wrong, only quicker to change.
    /// </summary>
    public static Guid Of(Assembly assembly) => Known.GetOrAdd(assembly, static assembly =>
    {
        string path = assembly.Location;
        if (path.Length == 0 || !File.Exists(path))
        {
            return assembly.ManifestModule.ModuleVersionId;
        }

        return Of(File.ReadAllBytes(path));
    });

    /// <summary>The id of the code in an assembly file's bytes.</summary>
    public static Guid Of(byte[] image)
    {
        byte[] code = (byte[])image.Clone();
        using var reader = new PEReader(new MemoryStream(image));
        PEHeaders headers = reader.PEHeaders;

        // The file's timestamp, which a deterministic build makes from the hash of the file.
        Array.Clear(code, headers.CoffHeaderStartOffset + 4, sizeof(uint));

        DirectoryEntry debug = headers.PEHeader!.DebugTableDirectory;
        if (debug.Size > 0 && headers.TryGetDirectoryOffset(debug, out int debugOffset))
        {
            foreach (DebugDirectoryEntry entry in reader.ReadDebugDirectory())
            {
                if (entry.DataSize > 0 && entry.DataPointer > 0)
                {
                    Array.Clear(code, entry.DataPointer, entry.DataSize);
                }
            }

            Array.Clear(code, debugOffset, debug.Size);
        }

        MetadataReader metadata = reader.GetMetadataReader();
        int guids = headers.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Guid);
        int mvid = MetadataTokens.GetHeapOffset(metadata.GetModuleDefinition().Mvid);
        Array.Clear(code, guids + (mvid - 1) * 16, 16);

        return new Guid(SHA256.HashData(code).AsSpan(0, 16));
    }
}
