using System.Buffers.Binary;
using System.Text;

namespace Rendition.Gpu;

/// <summary>What a shader binding holds, which decides the kind of resource bound to it.</summary>
internal enum ResourceKind
{
    UniformBuffer,
    StorageBuffer,
    SampledImage,
    StorageImage,
    Sampler,
    ImageWithSampler,
    Structure,
}

/// <summary>
/// Where a shader expects one resource: its group (descriptor set) and binding number, what kind it is,
/// and how many there are. A <see cref="Count"/> of 0 is an array without a size.
/// </summary>
internal readonly record struct ShaderBinding(int Group, int Binding, ResourceKind Kind, int Count);

/// <summary>A binding with the name the shader gives it.</summary>
internal readonly record struct NamedBinding(string Name, ShaderBinding Binding);

/// <summary>
/// The bindings of one compiled shader, by name, read from SPIR-V that keeps its names: the
/// <c>.layout.spv</c> files Shaders.targets writes beside each optimized <c>.spv</c>.
/// </summary>
internal sealed class ShaderLayout
{
    private readonly Dictionary<string, ShaderBinding> _byName;

    private ShaderLayout(Dictionary<string, ShaderBinding> byName, int constantsSize)
    {
        _byName = byName;
        ConstantsSize = constantsSize;
        Bindings = [.. byName.Select(pair => new NamedBinding(pair.Key, pair.Value))];
    }

    /// <summary>Every binding the shader declares, in no particular order.</summary>
    public IReadOnlyList<NamedBinding> Bindings { get; }

    /// <summary>How many bytes of push constants the shader reads, or 0 when it reads none.</summary>
    public int ConstantsSize { get; }

    public bool TryFind(string name, out ShaderBinding binding) => _byName.TryGetValue(name, out binding);

    public static ShaderLayout FromSpirv(ReadOnlySpan<byte> spirv)
    {
        const uint Magic = 0x07230203;
        if (spirv.Length < 20 || spirv.Length % 4 != 0 || BinaryPrimitives.ReadUInt32LittleEndian(spirv) != Magic)
        {
            throw new InvalidDataException("This is not SPIR-V: it does not start with the magic number.");
        }

        var module = SpirvModule.Read(spirv);
        var byName = new Dictionary<string, ShaderBinding>();
        int constantsSize = 0;
        foreach (var (id, pointerType) in module.Variables)
        {
            var (storage, type) = module.Pointers[pointerType];
            if (storage == SpirvModule.PushConstant)
            {
                constantsSize = module.SizeOf(type);
                continue;
            }

            if (!module.Sets.TryGetValue(id, out int set) || !module.BindingNumbers.TryGetValue(id, out int number))
            {
                continue;
            }

            var (kind, count) = module.Describe(storage, type);
            string name = module.Names.GetValueOrDefault(id) ?? "";
            if (name.Length == 0)
            {
                name = module.Names.GetValueOrDefault(type) ?? "";
            }

            byName[name] = new ShaderBinding(set, number, kind, count);
        }

        return new ShaderLayout(byName, constantsSize);
    }
}

/// <summary>The parts of a SPIR-V module <see cref="ShaderLayout"/> needs: names, decorations, types and variables.</summary>
internal sealed class SpirvModule
{
    public const int PushConstant = 9;

    private const int StorageBuffer = 12;

    private readonly Dictionary<int, int[]> _types = [];
    private readonly Dictionary<int, int> _constants = [];
    private readonly HashSet<int> _bufferBlocks = [];
    private readonly Dictionary<(int Struct, int Member), int> _offsets = [];

    public Dictionary<int, string> Names { get; } = [];

    public Dictionary<int, int> Sets { get; } = [];

    public Dictionary<int, int> BindingNumbers { get; } = [];

    /// <summary>Each pointer type's storage class and the type it points to.</summary>
    public Dictionary<int, (int Storage, int Type)> Pointers { get; } = [];

    /// <summary>Each global variable and its pointer type.</summary>
    public List<(int Id, int PointerType)> Variables { get; } = [];

    public static SpirvModule Read(ReadOnlySpan<byte> bytes)
    {
        var module = new SpirvModule();
        int count = bytes.Length / 4;
        var words = new int[count];
        for (int i = 0; i < count; i++)
        {
            words[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes[(i * 4)..]);
        }

        // The header is five words: magic, version, generator, bound and schema.
        int at = 5;
        while (at < count)
        {
            int length = (int)((uint)words[at] >> 16);
            if (length == 0 || at + length > count)
            {
                throw new InvalidDataException($"The SPIR-V instruction at word {at} runs past the end of the module.");
            }

            module.Take(words.AsSpan(at, length));
            at += length;
        }

        return module;
    }

    /// <summary>The kind of binding a variable of <paramref name="type"/> in <paramref name="storage"/> is, and how many.</summary>
    public (ResourceKind Kind, int Count) Describe(int storage, int type)
    {
        int count = 1;
        var op = _types[type];
        if (op[0] == OpTypeArray)
        {
            count = _constants.GetValueOrDefault(op[3], 1);
            type = op[2];
        }
        else if (op[0] == OpTypeRuntimeArray)
        {
            count = 0;
            type = op[2];
        }

        return (KindOf(storage, type), count);
    }

    private ResourceKind KindOf(int storage, int type)
    {
        var op = _types[type];
        return op[0] switch
        {
            OpTypeSampledImage => ResourceKind.ImageWithSampler,
            OpTypeSampler => ResourceKind.Sampler,
            OpTypeImage => op[7] == 2 ? ResourceKind.StorageImage : ResourceKind.SampledImage,
            OpTypeAccelerationStructure => ResourceKind.Structure,
            OpTypeStruct => BufferKind(storage, type),
            _ => throw new InvalidDataException($"A binding has a type SPIR-V calls opcode {op[0]}, which the GPU layer doesn't bind."),
        };
    }

    /// <summary>Old SPIR-V marks a storage buffer as a BufferBlock in Uniform storage; newer SPIR-V uses StorageBuffer storage.</summary>
    private ResourceKind BufferKind(int storage, int type) =>
        storage == StorageBuffer || _bufferBlocks.Contains(type) ? ResourceKind.StorageBuffer : ResourceKind.UniformBuffer;

    /// <summary>How many bytes a push-constant block takes: the end of its furthest member.</summary>
    public int SizeOf(int type)
    {
        var op = _types[type];
        switch (op[0])
        {
            case OpTypeInt or OpTypeFloat:
                return op[2] / 8;
            case OpTypeVector or OpTypeMatrix:
                return op[3] * SizeOf(op[2]);
            case OpTypeArray:
                return _constants.GetValueOrDefault(op[3], 1) * SizeOf(op[2]);
            case OpTypeStruct:
                int end = 0;
                for (int member = 0; member < op.Length - 2; member++)
                {
                    int offset = _offsets.GetValueOrDefault((type, member));
                    end = Math.Max(end, offset + SizeOf(op[2 + member]));
                }

                return end;
            default:
                throw new InvalidDataException($"Push constants hold a type SPIR-V calls opcode {op[0]}, which has no size here.");
        }
    }

    private const int OpName = 5;
    private const int OpTypeInt = 21;
    private const int OpTypeFloat = 22;
    private const int OpTypeVector = 23;
    private const int OpTypeMatrix = 24;
    private const int OpTypeImage = 25;
    private const int OpTypeSampler = 26;
    private const int OpTypeSampledImage = 27;
    private const int OpTypeArray = 28;
    private const int OpTypeRuntimeArray = 29;
    private const int OpTypeStruct = 30;
    private const int OpTypePointer = 32;
    private const int OpConstant = 43;
    private const int OpVariable = 59;
    private const int OpDecorate = 71;
    private const int OpMemberDecorate = 72;
    private const int OpTypeAccelerationStructure = 5341;

    private const int DecorationBufferBlock = 3;
    private const int DecorationBinding = 33;
    private const int DecorationDescriptorSet = 34;
    private const int DecorationOffset = 35;

    /// <summary>Notes one instruction. Word 0 holds its opcode in the low half; the rest are its operands.</summary>
    private void Take(ReadOnlySpan<int> instruction)
    {
        int opcode = instruction[0] & 0xFFFF;
        switch (opcode)
        {
            case OpName:
                Names[instruction[1]] = ReadString(instruction[2..]);
                break;
            case OpDecorate:
                Decorate(instruction[1], instruction[2], instruction.Length > 3 ? instruction[3] : 0);
                break;
            case OpMemberDecorate when instruction[3] == DecorationOffset:
                _offsets[(instruction[1], instruction[2])] = instruction[4];
                break;
            case OpTypePointer:
                Pointers[instruction[1]] = (instruction[2], instruction[3]);
                break;
            case OpConstant:
                _constants[instruction[2]] = instruction[3];
                break;
            case OpVariable when instruction[3] != 7 && instruction[3] != 6:
                // Function (7) and Private (6) variables are not bindings.
                Variables.Add((instruction[2], instruction[1]));
                break;
            case >= OpTypeInt and <= OpTypeStruct or OpTypeAccelerationStructure:
                // Keep the opcode in place of the result id, so op[0] says what the type is.
                int[] type = instruction.ToArray();
                type[0] = opcode;
                _types[instruction[1]] = type;
                break;
        }
    }

    private void Decorate(int target, int decoration, int value)
    {
        switch (decoration)
        {
            case DecorationDescriptorSet:
                Sets[target] = value;
                break;
            case DecorationBinding:
                BindingNumbers[target] = value;
                break;
            case DecorationBufferBlock:
                _bufferBlocks.Add(target);
                break;
        }
    }

    private static string ReadString(ReadOnlySpan<int> words)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(words);
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }
}
