using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


public static class StreamExtensions
{
    public static async Task<string> ReadAsStringAsync(this Stream stream, bool leaveOpen = false, Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;

        using var reader = new StreamReader(stream, encoding, leaveOpen: leaveOpen);

        var result = await reader.ReadToEndAsync();

        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        return result;
    }
    public static async Task<byte[]> ReadAsByteArrayAsync(this Stream stream, bool leaveOpen = false)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms);

            return ms.ToArray();
        }
    }
}

