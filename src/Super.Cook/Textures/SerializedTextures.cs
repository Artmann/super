using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Rendition;

namespace Super.Cook;

/// <summary>
/// The bytes each texture is written as, an object of its own as a model points to it, and their hash, made once
/// however many models share the texture. <see cref="TextureCompression"/> tells it about a compressed texture read
/// back from the step cache, whose bytes and hash it already has.
/// </summary>
internal static class SerializedTextures
{
    private static readonly ConditionalWeakTable<TextureData, Lazy<(string Hash, byte[] Bytes)>> Written = new();

    /// <summary>The bytes <paramref name="texture"/> is written as, and their SHA-256 in lowercase hex, made once per texture.</summary>
    public static (string Hash, byte[] Bytes) Of(TextureData texture)
    {
        return Written.GetValue(texture, key => new Lazy<(string, byte[])>(() =>
        {
            byte[] written = key.ToBytes();
            return (Convert.ToHexStringLower(SHA256.HashData(written)), written);
        })).Value;
    }

    /// <summary>
    /// Tells <see cref="Of"/> the bytes and hash of a texture read from them, such as a compressed texture taken from the
    /// step cache, so they aren't written and hashed again.
    /// </summary>
    public static void Know(TextureData texture, string hash, byte[] bytes)
    {
        Written.TryAdd(texture, new Lazy<(string, byte[])>((hash, bytes)));
    }
}
