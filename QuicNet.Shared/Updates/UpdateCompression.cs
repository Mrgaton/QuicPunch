using ZstdSharp;
using ZstdSharp.Unsafe;

namespace QuicNet.Updates;

public static class UpdateCompression
{
    public const int Level = 19, WindowLog = 27;
    public static async Task CompressAsync(string input, string output, CancellationToken ct = default)
    {
        await using var source = File.OpenRead(input);
        await using var destination = File.Create(output);
        await using var compressor = new CompressionStream(destination, Level);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, WindowLog);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
        await source.CopyToAsync(compressor, ct);
    }

    public static async Task DecompressAsync(string input, string output, long expectedSize, CancellationToken ct = default)
    {
        if (expectedSize <= 0 || expectedSize > 512L * 1024 * 1024) throw new InvalidDataException("Tamaño descomprimido inválido.");
        await using var source = File.OpenRead(input);
        await using var decoder = new DecompressionStream(source);
        decoder.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, WindowLog);
        await using var destination = File.Create(output);
        byte[] buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await decoder.ReadAsync(buffer, ct)) > 0)
        {
            total += count;
            if (total > expectedSize) throw new InvalidDataException("Zstandard supera el tamaño firmado.");
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (total != expectedSize) throw new InvalidDataException("La actualización descomprimida está incompleta.");
    }
}
