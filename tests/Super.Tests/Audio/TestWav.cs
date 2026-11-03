namespace Super.Tests.Audio;

/// <summary>Builds small WAV files in memory.</summary>
internal static class TestWav
{
    /// <summary>A WAV file. Samples are interleaved. With extraChunk, a LIST chunk comes before the data.</summary>
    public static byte[] Make(int rate, int channels, float[] samples, bool asFloat, bool extraChunk = false)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        int bytesPerSample = asFloat ? 4 : 2;
        int dataSize = samples.Length * bytesPerSample;
        byte[] list = [(byte)'I', (byte)'N', (byte)'F', (byte)'O', 1, 2, 3, 4, 5];

        w.Write("RIFF"u8);
        w.Write(0);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)(asFloat ? 3 : 1));
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * bytesPerSample);
        w.Write((short)(channels * bytesPerSample));
        w.Write((short)(bytesPerSample * 8));
        if (extraChunk)
        {
            w.Write("LIST"u8);
            w.Write(list.Length);
            w.Write(list);
            w.Write((byte)0); // odd-sized chunks are padded to even
        }

        w.Write("data"u8);
        w.Write(dataSize);
        foreach (float s in samples)
        {
            if (asFloat)
            {
                w.Write(s);
            }
            else
            {
                w.Write((short)Math.Round(s * 32768f));
            }
        }

        w.Flush();
        byte[] bytes = stream.ToArray();
        BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
        return bytes;
    }
}
